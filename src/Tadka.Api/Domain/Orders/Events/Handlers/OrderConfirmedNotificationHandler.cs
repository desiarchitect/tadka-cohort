using MediatR;

namespace Tadka.Api.Domain.Orders.Events.Handlers;

/// <summary>
/// Sample side-effect: notify the customer when their order is confirmed.
/// Today it just logs — but notice it lives OUTSIDE Order.Transition(). If sending the SMS
/// throws, the confirmed order is already saved; the failure does not roll back the transition.
/// At extraction this handler becomes a Notification service consuming an OrderConfirmed message.
///
/// Day 7 (ADR-022): now a MediatR <see cref="INotificationHandler{T}"/> — auto-registered, fanned
/// out by <c>IMediator.Publish</c>. Same post-commit semantics as the old dispatcher (ADR-013).
/// </summary>
public class OrderConfirmedNotificationHandler(ILogger<OrderConfirmedNotificationHandler> logger)
    : INotificationHandler<OrderConfirmedEvent>
{
    private readonly ILogger<OrderConfirmedNotificationHandler> _logger = logger;

    public Task Handle(OrderConfirmedEvent notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "📲 Notification: order {OrderId} confirmed — SMS sent to customer {CustomerId}.",
            notification.OrderId, notification.CustomerId);
        return Task.CompletedTask;
    }
}
