using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tadka.Api.Contracts;
using Tadka.Api.Contracts.Orders;
using Tadka.Api.Contracts.Restaurants;
using Tadka.Api.Auth;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Domain.Common;
using Tadka.Api.Infrastructure.Messaging;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Exceptions;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
[Authorize] // every order endpoint requires a valid JWT (ADR-030); ownership is checked per-action (ADR-031)
public class OrdersController(
    IOrderRepository orderRepository,
    OrderFactory orderFactory,
    IIdempotencyStore idempotencyStore,
    IMediator mediator,
    TadkaReadDbContext readDb,
    TadkaDbContext db) : ControllerBase
{
    private readonly IOrderRepository _orderRepository = orderRepository;
    private readonly OrderFactory _orderFactory = orderFactory;
    private readonly IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly IMediator _mediator = mediator; // ADR-022: publishes domain events; Payment reacts to OrderPlaced
    private readonly TadkaReadDbContext _read = readDb; // replica — order history (ADR-016)
    private readonly TadkaDbContext _db = db; // monolith-phase lookup of restaurant + menu for server-side pricing

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(
        [FromBody] CreateOrderRequest request,
        [FromServices] IValidator<CreateOrderRequest> validator)
    {
        var result = await validator.ValidateAsync(request);
        if (!result.IsValid)
            throw new ValidationException(result.Errors);

        // Idempotency (ADR-011): if the client sends an Idempotency-Key and we have already seen it,
        // return the order that key created — a double-tap / retry must never place a second order.
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existingOrderId = await _idempotencyStore.FindOrderIdAsync(idempotencyKey);
            if (existingOrderId is not null)
            {
                var existing = await _orderRepository.GetByIdAsync(existingOrderId.Value);
                return Ok(MapToResponse(existing!)); // replay → 200 with the original order
            }
        }

        // Load restaurant with menu to do server-side pricing (the client never sends a price).
        var restaurant = await _db.Restaurants.AsNoTracking()
            .Include(r => r.Menu)
            .FirstOrDefaultAsync(r => r.Id == request.RestaurantId);

        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), request.RestaurantId);

        var itemsRequest = request.Items.Select(i => (i.MenuItemId, i.Quantity, i.SpecialInstructions)).ToList();
        var address = new Address(
            request.DeliveryAddress.Line1,
            request.DeliveryAddress.Line2,
            request.DeliveryAddress.City,
            request.DeliveryAddress.Pincode,
            request.DeliveryAddress.Latitude,
            request.DeliveryAddress.Longitude);

        // A customer can only place an order as themselves (identity from the token, ADR-031); Admin may
        // place on behalf of any customerId in the body (e.g. support/ops).
        var effectiveCustomerId = User.IsAdmin() ? request.CustomerId : (User.UserId() ?? request.CustomerId);
        var orderResult = _orderFactory.Create(effectiveCustomerId, restaurant, itemsRequest, address);
        if (orderResult.IsFailure)
            // An unavailable item, or an item not on this restaurant's menu, is a
            // domain-rule violation (valid request, breaks a business rule) → 422,
            // consistent with illegal state transitions. Malformed input is 400,
            // already handled by validation above.
            return Problem(detail: orderResult.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Order Cannot Be Placed");

        var order = orderResult.Value;

        _orderRepository.Add(order);

        // The key→order mapping is staged on the SAME DbContext, so it commits in the SAME
        // transaction as the order: either both land or neither does.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            _idempotencyStore.Record(idempotencyKey, order.Id);

        // Transactional Outbox (ADR-028): stage the cross-service `order-placed` event on the SAME
        // DbContext as the order, so it commits in the SAME transaction — the event can never be lost on
        // a crash (no dual-write problem). The OutboxRelay (ADR-027) publishes it to Kafka; the Payment
        // service consumes it and charges OFF the request path. POST /orders still returns in ms.
        var placed = new OrderPlacedMessage(Guid.NewGuid(), order.Id, order.TotalAmount.Amount, order.TotalAmount.Currency, order.CustomerId);
        _db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Topic = Topics.OrderPlaced,
            Key = order.Id.ToString(),
            Payload = JsonSerializer.Serialize(placed)
        });

        try
        {
            await _orderRepository.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Concurrent replay: two requests with the same key both missed the Find above and
            // both inserted. The unique constraint on the key (its PK) is the real race fix
            // (ADR-011) — no second order is ever created. But the loser must still honour the
            // idempotent contract: return the WINNER's order as 200, not bubble a 500. Without
            // this, a concurrent double-tap 500s while a sequential one returns 200 — a subtle,
            // test-invisible inconsistency, since the sequential path is served by the Find above.
            _db.ChangeTracker.Clear();
            var winnerId = await _idempotencyStore.FindOrderIdAsync(idempotencyKey);
            if (winnerId is null)
                throw;
            var winner = await _orderRepository.GetByIdAsync(winnerId.Value);
            return Ok(MapToResponse(winner!));
        }

        // Publish in-process domain events AFTER commit (ADR-013): live-tracking/notification handlers.
        // The cross-service payment flow now rides the Outbox above (Kafka), not an in-process handler.
        await PublishEventsAsync(order);

        return CreatedAtAction(nameof(GetById), new { id = order.Id }, MapToResponse(order));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id)
    {
        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

        // Resource ownership (ADR-031): you can only read your OWN order (Admin sees all).
        if (!User.IsAdmin() && order.CustomerId != User.UserId())
            return Forbid();

        return Ok(MapToResponse(order));
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<OrderResponse>>> GetByCustomer(
        [FromQuery] Guid? customerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);

        // A customer only sees THEIR OWN history (ADR-031); Admin may query any customerId.
        if (!User.IsAdmin())
            customerId = User.UserId();

        // Order *history* is read-heavy and tolerates slight replication lag, so it reads from the
        // replica (ADR-016). The (customer_id, created_at DESC) index (ADR-014) keeps it fast on a
        // large orders table. Contrast GET /orders/{id} below, which stays on the primary so a
        // customer always sees the order they just placed (read-your-writes).
        var query = _read.Orders.Include(o => o.Items).AsQueryable();
        if (customerId.HasValue)
            query = query.Where(o => o.CustomerId == customerId.Value);

        var totalCount = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var response = orders.Select(MapToResponse).ToList();
        return Ok(new PagedResponse<OrderResponse>(response, page, pageSize, totalCount));
    }

    [Authorize(Roles = "RestaurantOwner,DeliveryAgent,Admin")] // kitchen/rider/ops advance status — not customers (ADR-031)
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateOrderStatusRequest request)
    {
        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

        // Resource ownership for the kitchen side (ADR-031): a RestaurantOwner may only advance orders
        // placed at THEIR OWN restaurant — RBAC above says "owners can advance status", this says "but
        // only for your own restaurant's orders". Admin bypasses, same as everywhere else.
        // DeliveryAgent has no per-rider assignment model yet (no "which rider is on this order" data
        // exists anywhere in the schema), so a DeliveryAgent can still advance any order's status — a
        // named, honest gap, not an oversight: adding rider assignment is a bigger feature than a single
        // ownership check, and belongs with the Delivery service extraction, not bolted on here.
        if (User.IsInRole("RestaurantOwner") && !User.IsAdmin() && order.RestaurantId != User.OwnedRestaurantId())
            return Forbid();

        if (!Enum.TryParse<OrderStatus>(request.Status, ignoreCase: true, out var newStatus))
            throw new DomainException($"Invalid status '{request.Status}'. Valid values: {string.Join(", ", Enum.GetNames<OrderStatus>())}");

        var result = order.Transition(newStatus);
        if (result.IsFailure)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid State Transition");

        // A concurrent PATCH may have moved the order since we read it — SaveChanges then throws
        // DbUpdateConcurrencyException (xmin mismatch) → 409 via the middleware (ADR-012).
        await _orderRepository.SaveChangesAsync();

        await PublishEventsAsync(order);
        return NoContent();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult> Cancel(
        Guid id,
        [FromBody] CancelOrderRequest request)
    {
        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

        // Only the order's customer (or Admin) may cancel it (ADR-031).
        if (!User.IsAdmin() && order.CustomerId != User.UserId())
            return Forbid();

        var result = order.Cancel(request.Reason ?? string.Empty);
        if (result.IsFailure)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid State Transition");

        await _orderRepository.SaveChangesAsync();
        return NoContent();
    }

    // Publish each domain event the aggregate raised, then clear them. MediatR fans each out to
    // every INotificationHandler<T> (ADR-022). Called only AFTER SaveChanges (ADR-013).
    // Snapshot-then-clear BEFORE publishing: a handler may (synchronously, ADR-023 sync mode) trigger a
    // chain that mutates this same aggregate's event list — iterating a live collection would throw.
    private async Task PublishEventsAsync(Order order)
    {
        var events = order.DomainEvents.ToList();
        order.ClearDomainEvents();
        foreach (var domainEvent in events)
            await _mediator.Publish(domainEvent);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static OrderResponse MapToResponse(Order o) => new(
        o.Id,
        o.CustomerId,
        o.RestaurantId,
        o.Status.ToString(),
        o.Items.Select(i => new OrderItemResponse(
            i.Id,
            i.MenuItemId,
            i.Name,
            i.Quantity,
            new MoneyResponse(i.UnitPrice.Amount, i.UnitPrice.Currency),
            i.SpecialInstructions)).ToList(),
        new MoneyResponse(o.TotalAmount.Amount, o.TotalAmount.Currency),
        new RestaurantAddressResponse(
            o.DeliveryAddress.Line1,
            o.DeliveryAddress.Line2,
            o.DeliveryAddress.City,
            o.DeliveryAddress.Pincode,
            o.DeliveryAddress.Latitude,
            o.DeliveryAddress.Longitude),
        o.CreatedAt,
        o.ConfirmedAt,
        o.DeliveredAt,
        o.CancelledAt,
        o.CancellationReason);
}
