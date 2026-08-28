using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Contracts;
using Tadka.Api.Contracts.Orders;
using Tadka.Api.Contracts.Restaurants;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Exceptions;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
public class OrdersController(TadkaDbContext db, OrderFactory orderFactory) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(
        [FromBody] CreateOrderRequest request,
        [FromServices] IValidator<CreateOrderRequest> validator)
    {
        var result = await validator.ValidateAsync(request);
        if (!result.IsValid)
            throw new ValidationException(result.Errors);

        // Load restaurant with menu to do server-side pricing (the client never sends a price).
        var restaurant = await db.Restaurants.AsNoTracking()
            .Include(r => r.Menu)
            .FirstOrDefaultAsync(r => r.Id == request.RestaurantId);

        if (restaurant is null)
            throw new NotFoundException(nameof(Restaurant), request.RestaurantId);

        var itemsRequest = request.Items.Select(i => (i.MenuItemId, i.Quantity, i.SpecialInstructions)).ToList();
        var delivery = request.DeliveryAddress!;
        var address = new Address(
            delivery.Line1,
            delivery.Line2,
            delivery.City,
            delivery.Pincode,
            delivery.Latitude,
            delivery.Longitude);

        var orderResult = orderFactory.Create(request.CustomerId, restaurant, itemsRequest, address);
        if (orderResult.IsFailure)
            return Problem(detail: orderResult.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Order Cannot Be Placed");

        var order = orderResult.Value;
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Payment is intentionally NOT processed here (Day 7).
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, MapToResponse(order));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id);
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

        var query = db.Orders.AsNoTracking().Include(o => o.Items).AsQueryable();
        if (customerId.HasValue)
            query = query.Where(o => o.CustomerId == customerId.Value);

        var totalCount = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new PagedResponse<OrderResponse>(orders.Select(MapToResponse).ToList(), page, pageSize, totalCount));
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateOrderStatusRequest request)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

        if (!Enum.TryParse<OrderStatus>(request.Status, ignoreCase: true, out var newStatus))
            throw new DomainException($"Invalid status '{request.Status}'. Valid values: {string.Join(", ", Enum.GetNames<OrderStatus>())}");

        var result = order.Transition(newStatus);
        if (result.IsFailure)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid State Transition");

        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult> Cancel(
        Guid id,
        [FromBody] CancelOrderRequest request)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null)
            throw new NotFoundException(nameof(Order), id);

        var result = order.Cancel(request.Reason ?? string.Empty);
        if (result.IsFailure)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid State Transition");

        await db.SaveChangesAsync();
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
            o.DeliveryAddress.Line2 ?? "",
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
