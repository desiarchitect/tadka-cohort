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
    TadkaDbContext db) : ControllerBase
{
    private readonly IOrderRepository _orderRepository = orderRepository;
    private readonly OrderFactory _orderFactory = orderFactory;
    private readonly TadkaDbContext _db = db; // monolith-phase lookup of restaurant + menu for server-side pricing

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(
        [FromBody] CreateOrderRequest request,
        [FromServices] IValidator<CreateOrderRequest> validator)
    {
        var result = await validator.ValidateAsync(request);
        if (!result.IsValid)
            throw new ValidationException(result.Errors);

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
            return Problem(detail: orderResult.Error, statusCode: StatusCodes.Status400BadRequest, title: "Invalid Order Creation");

        var order = orderResult.Value;

        _orderRepository.Add(order);
        await _orderRepository.SaveChangesAsync();

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

        await _orderRepository.SaveChangesAsync();
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
