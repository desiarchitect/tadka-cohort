using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders.Events.Handlers;

/// <summary>
/// Sample side-effect: notify the customer when their order is confirmed.
/// Today it just logs — but notice it lives OUTSIDE Order.Transition(). If sending the SMS
/// throws, the confirmed order is already saved; the failure does not roll back the transition.
/// At extraction this handler becomes a Notification service consuming an OrderConfirmed message.
///
/// `Demo:NotificationDelayMs` (default 0) stands in for a slow SMS gateway — the Day 4 lock-hold
/// beat (ADR-045) uses it together with `Demo:DispatchEventsBeforeCommit` to make the delay long
/// enough to observe in `pg_locks`. It is a no-op at the shipped default.
/// </summary>
public class OrderConfirmedNotificationHandler(ILogger<OrderConfirmedNotificationHandler> logger, IConfiguration configuration)
    : IDomainEventHandler<OrderConfirmedEvent>
{
    private readonly ILogger<OrderConfirmedNotificationHandler> _logger = logger;
    private readonly IConfiguration _configuration = configuration;

    public async Task HandleAsync(OrderConfirmedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var delayMs = _configuration.GetValue("Demo:NotificationDelayMs", 0);
        if (delayMs > 0)
            await Task.Delay(delayMs, cancellationToken);

        _logger.LogInformation(
            "📲 Notification: order {OrderId} confirmed — SMS sent to customer {CustomerId}.",
            domainEvent.OrderId, domainEvent.CustomerId);
    }
}
