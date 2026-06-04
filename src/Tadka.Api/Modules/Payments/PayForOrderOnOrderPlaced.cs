using MediatR;
using Microsoft.Extensions.Options;
using Tadka.Api.Domain.Orders.Events;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// Ordering's "an order was placed" announcement, picked up to trigger payment (ADR-022/023). Ordering has
/// no idea Payment is now a separate service — it just raises <see cref="OrderPlacedEvent"/>. This handler
/// enqueues the charge for the background processor (which calls the Payment service over HTTP), so
/// <c>POST /orders</c> stays decoupled from payment and returns in milliseconds — the Day-7 async win,
/// preserved across the Day-8 extraction.
/// </summary>
public sealed class PayForOrderOnOrderPlaced(
    IOptions<PaymentClientOptions> options,
    PaymentWorkChannel channel,
    ILogger<PayForOrderOnOrderPlaced> logger) : INotificationHandler<OrderPlacedEvent>
{
    public async Task Handle(OrderPlacedEvent notification, CancellationToken cancellationToken)
    {
        if (options.Value.IsDisabled)
            return; // payment integration switched off (feature flag / test seam)

        var amount = new Money(notification.Amount, notification.Currency);
        await channel.EnqueueAsync(new PaymentWorkItem(notification.OrderId, amount), cancellationToken);
        logger.LogInformation("Order {OrderId} queued for async payment (→ Payment service over HTTP).", notification.OrderId);
    }
}
