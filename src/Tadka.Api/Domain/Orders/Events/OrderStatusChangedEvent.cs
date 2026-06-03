using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders.Events;

/// <summary>Raised on every successful state transition. Drives live tracking (ADR-020).</summary>
public sealed record OrderStatusChangedEvent(Guid OrderId, OrderStatus Status) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
