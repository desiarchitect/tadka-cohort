using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    TadkaReadDbContext readDb,
    TadkaDbContext db) : ControllerBase
{
    private readonly IOrderRepository _orderRepository = orderRepository;
    private readonly OrderFactory _orderFactory = orderFactory;
    private readonly IIdempotencyStore _idempotencyStore = idempotencyStore;
    private readonly IDomainEventDispatcher _eventDispatcher = eventDispatcher;
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

        await _orderRepository.SaveChangesAsync();

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

    // Day 5, Beat 4 (keyset pagination): OFFSET N still makes Postgres walk and discard N rows
    // before it can return page N+1 — cheap at page 2, expensive at page 10,000. Keyset
    // pagination instead asks "give me the next rows AFTER this exact position" using the same
    // (customer_id, created_at DESC) index (ADR-014) as an inequality, not a Skip — so page 2 and
    // page 10,000 cost the same. The trade-off: no jump-to-page-N and no total count for free
    // (computing one means scanning/counting the whole matching set — the cost this exists to
    // avoid). Use this for infinite-scroll order history; keep GetByCustomer's OFFSET for small,
    // page-numbered admin views where jump-to-page matters more than large-N cost.
    [HttpGet("history")]
    public async Task<ActionResult<CursorPageResponse<OrderResponse>>> GetByCustomerCursor(
        [FromQuery] Guid customerId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 10)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = _read.Orders.Include(o => o.Items)
            .Where(o => o.CustomerId == customerId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            DateTime createdAt;
            Guid id;
            try
            {
                (createdAt, id) = OrderCursor.Decode(cursor);
            }
            catch (ArgumentException)
            {
                // A malformed cursor is bad CLIENT input, not a server fault — a client that
                // hand-constructs or corrupts an opaque cursor gets a 400, never a 500.
                return Problem(detail: "Invalid cursor.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid Cursor");
            }
            // Same tie-breaker as the ORDER BY below: strictly older, or same instant with a
            // strictly smaller id. Matches the composite index left-to-right, no Sort node needed.
            query = query.Where(o =>
                o.CreatedAt < createdAt || (o.CreatedAt == createdAt && o.Id.CompareTo(id) < 0));
        }

        // +1 "lookahead" row tells us whether there IS a next page without a separate COUNT query.
        var page = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(pageSize + 1)
            .ToListAsync();

        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page.Take(pageSize) : page).ToList();
        var nextCursor = hasMore ? OrderCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null;

        return Ok(new CursorPageResponse<OrderResponse>(items.Select(MapToResponse).ToList(), nextCursor));
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
