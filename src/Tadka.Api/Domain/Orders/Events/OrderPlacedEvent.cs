using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders.Events;

/// <summary>
/// Raised when an order is placed. Carries the data a consumer needs to act WITHOUT reaching back
/// into Ordering's tables — notably <see cref="Amount"/>/<see cref="Currency"/>, so the Payment module
/// (ADR-022/023) can charge from the event alone. "Events carry their own data" is what lets the
/// consumer become a separate service (Day 8 / Week 5) that has no access to the order database.
/// </summary>
public sealed record OrderPlacedEvent(
    Guid OrderId,
    Guid CustomerId,
    Guid RestaurantId,
    decimal Amount,
    string Currency) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
