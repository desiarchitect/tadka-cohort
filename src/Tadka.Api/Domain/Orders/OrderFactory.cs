using Tadka.Api.Domain.Common;
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

        return Result.Success(order);
    }
}
