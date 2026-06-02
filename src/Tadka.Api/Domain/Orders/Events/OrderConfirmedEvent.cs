using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders.Events;

public sealed record OrderConfirmedEvent(Guid OrderId, Guid CustomerId) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
