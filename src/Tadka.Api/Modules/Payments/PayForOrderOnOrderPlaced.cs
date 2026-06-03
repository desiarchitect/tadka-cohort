using MediatR;
using Microsoft.Extensions.Options;
using Tadka.Api.Domain.Orders.Events;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// The Payment module's reaction to <see cref="OrderPlacedEvent"/> (ADR-022). This is the entire
/// inbound contract between Ordering and Payment — Ordering just announces "an order was placed";
/// it has no idea Payment exists.
///
/// <para><b>Async</b> (ADR-023, shipped): enqueue the charge and return immediately, so the order
/// request is never blocked by the gateway. <b>Synchronous</b> (the brownout baseline): charge inline,
/// inside the order-creation request — exactly the coupling Week 4 exists to break.</para>
/// </summary>
public sealed class PayForOrderOnOrderPlaced(
    IOptions<PaymentOptions> options,
    PaymentWorkChannel channel,
    PaymentService paymentService,
    ILogger<PayForOrderOnOrderPlaced> logger) : INotificationHandler<OrderPlacedEvent>
{
    public async Task Handle(OrderPlacedEvent notification, CancellationToken cancellationToken)
    {
        if (options.Value.IsDisabled)
            return; // payment integration switched off (feature flag / test seam)

        var amount = new Money(notification.Amount, notification.Currency);

        if (options.Value.IsSynchronous)
        {
            // ⚠️ The naive baseline: payment happens INSIDE the order request. A slow gateway now holds
            // this request (and primary resources) for its whole duration — the brownout (ADR-021/023).
            logger.LogWarning("⚠️ SYNCHRONOUS payment for order {OrderId} — charging inside the request.", notification.OrderId);
            await paymentService.ProcessAsync(notification.OrderId, amount, cancellationToken);
            return;
        }

        // Shipped path: hand off to the background processor and return — POST /orders is now decoupled
        // from the gateway entirely.
        await channel.EnqueueAsync(new PaymentWorkItem(notification.OrderId, amount), cancellationToken);
        logger.LogInformation("Order {OrderId} queued for async payment.", notification.OrderId);
    }
}
