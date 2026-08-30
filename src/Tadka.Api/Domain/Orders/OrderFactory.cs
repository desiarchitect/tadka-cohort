using Tadka.Api.Domain.Common;
using Tadka.Api.Domain.Orders.Events;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Orders;

public class OrderFactory
{
    public Result<Order> Create(Guid customerId, Restaurant restaurant, List<(Guid MenuItemId, int Quantity, string? SpecialInstructions)> requestedItems, Address deliveryAddress)
    {
        var menuItemIds = requestedItems.Select(i => i.MenuItemId).ToHashSet();
        var menuLookup = restaurant.Menu
            .Where(m => menuItemIds.Contains(m.Id))
            .ToDictionary(m => m.Id);

        var orderItems = new List<OrderItem>();
        foreach (var item in requestedItems)
        {
            if (!menuLookup.TryGetValue(item.MenuItemId, out var menuItem))
                return Result.Failure<Order>($"Menu item '{item.MenuItemId}' not found in restaurant '{restaurant.Name}'.");

            if (!menuItem.IsAvailable)
                return Result.Failure<Order>($"'{menuItem.Name}' is currently unavailable.");

            orderItems.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                MenuItemId = menuItem.Id,
                Name = menuItem.Name,
                Quantity = item.Quantity,
                UnitPrice = menuItem.Price,
                SpecialInstructions = item.SpecialInstructions
            });
        }

        var totalAmount = orderItems.Sum(i => i.UnitPrice.Amount * i.Quantity);

        // The aggregate constructor stamps Id/CreatedAt and starts the order in Created — the
        // factory's job is the server-side pricing and validation above, not setting raw state.
        var order = new Order(customerId, restaurant.Id, orderItems, new Money(totalAmount), deliveryAddress);

        order.Raise(new OrderPlacedEvent(order.Id, order.CustomerId, order.RestaurantId));

        return Result.Success(order);
    }
}
