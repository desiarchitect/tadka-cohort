using MediatR;
using Tadka.Api.Infrastructure.Realtime;

namespace Tadka.Api.Domain.Orders.Events.Handlers;

/// <summary>
/// Publishes each order status change to the live-tracking backplane (ADR-020). Dispatched AFTER the
/// transition is persisted (ADR-013), so a publish failure never rolls back a committed transition.
/// In-process today; becomes a Kafka producer at extraction (Week 5) — the handler shape is the same.
///
/// Day 7 (ADR-022): a MediatR <see cref="INotificationHandler{T}"/>. This is also the seam that makes
/// async payment feel instant — when payment settles, the order's status change rides this stream (SSE).
/// </summary>
public class OrderStatusChangedTrackingHandler(IOrderTrackingBus bus) : INotificationHandler<OrderStatusChangedEvent>
{
    private readonly IOrderTrackingBus _bus = bus;

    public Task Handle(OrderStatusChangedEvent notification, CancellationToken cancellationToken)
        => _bus.PublishAsync(
            new OrderTrackingEvent(
                notification.OrderId,
                notification.Status.ToString(),
                $"Your order is now {notification.Status}.",
                DateTime.UtcNow),
            cancellationToken);
}
