using Tadka.Api.Domain.Common;
using Tadka.Api.Domain.Orders.Events;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Orders;

public class OrderFactory
{
    /// <summary>
    /// Build a priced order. Pricing comes from a <see cref="RestaurantPricing"/> projection (ADR-037) —
    /// the local read model by default — NOT the Restaurant aggregate (which lives in its own service now,
    /// ADR-036). The server prices every line from this projection; the client never sends a price.
    /// </summary>
    public Result<Order> Create(Guid customerId, RestaurantPricing restaurant, List<(Guid MenuItemId, int Quantity, string? SpecialInstructions)> requestedItems, Address deliveryAddress)
    {
        if (!restaurant.IsActive)
            return Result.Failure<Order>($"Restaurant '{restaurant.Id}' is not currently accepting orders.");

        var menuItemIds = requestedItems.Select(i => i.MenuItemId).ToHashSet();
        var menuLookup = restaurant.Menu
            .Where(m => menuItemIds.Contains(m.MenuItemId))
            .ToDictionary(m => m.MenuItemId);

        var orderItems = new List<OrderItem>();
        foreach (var item in requestedItems)
        {
            if (!menuLookup.TryGetValue(item.MenuItemId, out var menuItem))
                return Result.Failure<Order>($"Menu item '{item.MenuItemId}' not found in restaurant '{restaurant.Id}'.");

            if (!menuItem.IsAvailable)
                return Result.Failure<Order>($"'{menuItem.Name}' is currently unavailable.");

            orderItems.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                MenuItemId = menuItem.MenuItemId,
                Name = menuItem.Name,
                Quantity = item.Quantity,
                UnitPrice = new Money(menuItem.Amount, menuItem.Currency),
                SpecialInstructions = item.SpecialInstructions
            });
        }

        var totalAmount = orderItems.Sum(i => i.UnitPrice.Amount * i.Quantity);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            RestaurantId = restaurant.Id,
            Status = OrderStatus.Created,
            Items = orderItems,
            TotalAmount = new Money(totalAmount),
            DeliveryAddress = deliveryAddress,
            CreatedAt = DateTime.UtcNow
        };

        order.Raise(new OrderPlacedEvent(
            order.Id, order.CustomerId, order.RestaurantId,
            order.TotalAmount.Amount, order.TotalAmount.Currency));

        return Result.Success(order);
    }
}
