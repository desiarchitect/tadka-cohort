using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders.Events.Handlers;

/// <summary>
/// Sample side-effect: notify the customer when their order is confirmed.
/// Today it just logs — but notice it lives OUTSIDE Order.Transition(). If sending the SMS
/// throws, the confirmed order is already saved; the failure does not roll back the transition.
/// At extraction this handler becomes a Notification service consuming an OrderConfirmed message.
/// </summary>
public class OrderConfirmedNotificationHandler(ILogger<OrderConfirmedNotificationHandler> logger)
    : IDomainEventHandler<OrderConfirmedEvent>
{
    private readonly ILogger<OrderConfirmedNotificationHandler> _logger = logger;

    public Task HandleAsync(OrderConfirmedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "📲 Notification: order {OrderId} confirmed — SMS sent to customer {CustomerId}.",
            domainEvent.OrderId, domainEvent.CustomerId);
        return Task.CompletedTask;
    }
}
