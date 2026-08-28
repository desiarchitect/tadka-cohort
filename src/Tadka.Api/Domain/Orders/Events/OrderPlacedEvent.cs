using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders.Events;

public sealed record OrderPlacedEvent(Guid OrderId, Guid CustomerId, Guid RestaurantId) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
