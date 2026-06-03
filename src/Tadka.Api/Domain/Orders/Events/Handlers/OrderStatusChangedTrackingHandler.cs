using Tadka.Api.Domain.Common;
using Tadka.Api.Infrastructure.Realtime;

namespace Tadka.Api.Domain.Orders.Events.Handlers;

/// <summary>
/// Publishes each order status change to the live-tracking backplane (ADR-020). Dispatched AFTER the
/// transition is persisted (ADR-013), so a publish failure never rolls back a committed transition.
/// In-process today; becomes a Kafka producer at extraction (Week 5) — the handler shape is the same.
/// </summary>
public class OrderStatusChangedTrackingHandler(IOrderTrackingBus bus) : IDomainEventHandler<OrderStatusChangedEvent>
{
    private readonly IOrderTrackingBus _bus = bus;

    public Task HandleAsync(OrderStatusChangedEvent domainEvent, CancellationToken cancellationToken = default)
        => _bus.PublishAsync(
            new OrderTrackingEvent(
                domainEvent.OrderId,
                domainEvent.Status.ToString(),
                $"Your order is now {domainEvent.Status}.",
                DateTime.UtcNow),
            cancellationToken);
}
