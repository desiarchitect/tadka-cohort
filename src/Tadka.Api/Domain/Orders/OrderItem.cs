using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Orders;

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public Money UnitPrice { get; set; } = null!;
    public string? SpecialInstructions { get; set; }
}
