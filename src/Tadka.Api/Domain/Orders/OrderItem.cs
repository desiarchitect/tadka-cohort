using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Orders;

// Entity inside the Order aggregate. It has identity (Id) but no independent
// lifecycle — it lives and dies with its Order.
// Name and UnitPrice are SNAPSHOTS captured at order time: if the restaurant
// renames the dish or changes the price tomorrow, this order is unchanged.
// That's historical accuracy, not duplication.
public class OrderItem
{
    public Guid Id { get; set; }
    public Guid MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public Money UnitPrice { get; set; } = null!;
    public string? SpecialInstructions { get; set; }
}
