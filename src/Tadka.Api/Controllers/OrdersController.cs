using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tadka.Api.Contracts;
using Tadka.Api.Contracts.Orders;
using Tadka.Api.Contracts.Restaurants;
using Tadka.Api.Data;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Domain.Common;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Exceptions;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
public class OrdersController(
    IOrderRepository orderRepository,
    OrderFactory orderFactory,
    IIdempotencyStore idempotencyStore,
    IDomainEventDispatcher eventDispatcher,
    TadkaDbContext db,
    IConfiguration configuration) : ControllerBase
{
    private readonly IOrderRepository _orderRepository = orderRepository;
    private readonly OrderFactory _orderFactory = orderFactory;
    private readonly IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly IDomainEventDispatcher _eventDispatcher = eventDispatcher;
    private readonly TadkaDbContext _db = db; // monolith-phase lookup of restaurant + menu for server-side pricing
    private readonly IConfiguration _configuration = configuration;

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

        var orderResult = _orderFactory.Create(request.CustomerId, restaurant, itemsRequest, address);
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

        try
        {
            await _orderRepository.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Concurrent replay: both requests missed the Find, both inserted. The unique
            // constraint on the key is the actual race fix (ADR-011). The loser must return
            // the winner's order as 200 — not 500.
            _db.ChangeTracker.Clear();
            var winnerId = await _idempotencyStore.FindOrderIdAsync(idempotencyKey);
            if (winnerId is null)
                throw;
            var winner = await _orderRepository.GetByIdAsync(winnerId.Value);
            return Ok(MapToResponse(winner!));
        }

        // Dispatch domain events AFTER the state is committed (ADR-013): a failed side-effect
        // (e.g. notification) must not roll back a persisted order.
        await _eventDispatcher.DispatchAsync(order.DomainEvents);
        order.ClearDomainEvents();

        // Payment is intentionally NOT processed here yet (see Day 7). Doing it
        // synchronously inside order creation would couple ordering to a slow
        // external gateway — the exact failure we study and fix in Week 4.
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, MapToResponse(order));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id)
    {
        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

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

        var totalCount = customerId.HasValue
            ? await _orderRepository.CountByCustomerIdAsync(customerId.Value)
            : await _orderRepository.CountAllAsync();

        var orders = customerId.HasValue
            ? await _orderRepository.GetByCustomerIdAsync(customerId.Value, page, pageSize)
            : await _orderRepository.GetAllAsync(page, pageSize);

        var response = orders.Select(MapToResponse).ToList();
        return Ok(new PagedResponse<OrderResponse>(response, page, pageSize, totalCount));
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateOrderStatusRequest request)
    {
        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

        if (!Enum.TryParse<OrderStatus>(request.Status, ignoreCase: true, out var newStatus))
            throw new DomainException($"Invalid status '{request.Status}'. Valid values: {string.Join(", ", Enum.GetNames<OrderStatus>())}");

        var result = order.Transition(newStatus);
        if (result.IsFailure)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid State Transition");

        // Day 4 break kit, Demo 6 (ADR-045 lock-hold beat): "Demo:DispatchEventsBeforeCommit"
        // reproduces the bug ADR-013 already fixed, on demand, for teaching. Default is false —
        // the correct, shipped behavior (dispatch AFTER commit, below). Flip it to true and an
        // EXPLICIT transaction is kept open across the slow notification handler
        // (Demo:NotificationDelayMs): SaveChanges flushes the UPDATE (taking the order row's
        // write lock) but does NOT commit, the handler runs next while that lock is still held,
        // and only then do we commit. A concurrent PATCH on the SAME order blocks for the
        // handler's whole duration. `pg_locks` shows exactly who is waiting on whom (see
        // break-kit-day-04.md, Demo 6).
        if (_configuration.GetValue("Demo:DispatchEventsBeforeCommit", false))
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            await _orderRepository.SaveChangesAsync(); // UPDATE runs, lock taken, transaction NOT yet committed
            await _eventDispatcher.DispatchAsync(order.DomainEvents); // the lock is held for this entire call
            await tx.CommitAsync(); // lock released here — too late, if the handler was slow
            order.ClearDomainEvents();
            return NoContent();
        }

        // A concurrent PATCH may have moved the order since we read it — SaveChanges then throws
        // DbUpdateConcurrencyException (xmin mismatch) → 409 via the middleware (ADR-012).
        await _orderRepository.SaveChangesAsync();

        await _eventDispatcher.DispatchAsync(order.DomainEvents);
        order.ClearDomainEvents();
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

        var result = order.Cancel(request.Reason ?? string.Empty);
        if (result.IsFailure)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid State Transition");

        await _orderRepository.SaveChangesAsync();
        return NoContent();
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
