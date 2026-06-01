using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Orders;

// Aggregate Root. An Order is a consistency boundary: its items, total, and
// delivery address are always valid together. You never create an OrderItem
// outside of an Order.
public class Order
{
    public Guid Id { get; set; }

    // Cross-domain references are by ID only — no FK to identity.users or
    // restaurant.restaurants (see ADR-008). Names are snapshotted at order time.
    public Guid CustomerId { get; set; }
    public Guid RestaurantId { get; set; }

    public OrderStatus Status { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public Money TotalAmount { get; set; } = null!;
    public Address DeliveryAddress { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}
