using MediatR;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Domain.Common.Events;
using Tadka.Api.Domain.Payments;
using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Infrastructure.Resilience;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// Charges an order through the Polly-wrapped gateway and records the outcome, then announces it
/// (<see cref="PaymentCompletedEvent"/>/<see cref="PaymentFailedEvent"/>) so Ordering can react.
/// The SAME code runs whether invoked synchronously (the brownout baseline) or from the background
/// processor (ADR-023) — only WHERE it runs changes. Idempotent: one payment per order (unique index).
/// </summary>
public sealed class PaymentService(
    PaymentDbContext db,
    IPaymentGateway gateway,
    PaymentResiliencePipeline resilience,
    IMediator mediator,
    ILogger<PaymentService> logger)
{
    public async Task ProcessAsync(Guid orderId, Money amount, CancellationToken cancellationToken = default)
    {
        // Idempotency (ADR-023): if a payment row already exists for this order, don't charge again.
        // The unique index on order_id is the hard guard; this check avoids a redundant gateway call.
        if (await db.Payments.AnyAsync(p => p.OrderId == orderId, cancellationToken))
        {
            logger.LogInformation("Payment for order {OrderId} already exists; skipping (idempotent).", orderId);
            return;
        }

        var payment = new Payment
        {
            OrderId = orderId,
            Amount = amount,
            Method = "UPI",
            Status = PaymentStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken); // a Pending row exists before we call the gateway

        try
        {
            // The ONLY call to something we don't own — always through the pipeline (ADR-021).
            var reference = await resilience.Pipeline.ExecuteAsync(
                async token => await gateway.ChargeAsync(orderId, amount, token),
                cancellationToken);

            payment.Status = PaymentStatus.Completed;
            payment.GatewayReference = reference;
            payment.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("💳 Payment COMPLETED for order {OrderId} (ref {Reference}).", orderId, reference);
            await mediator.Publish(new PaymentCompletedEvent(orderId, reference), cancellationToken);
        }
        catch (Exception ex)
        {
            // Timeout (slow gateway), bulkhead rejection (too many in flight), or a decline — all land
            // here as a FAST, CONTAINED failure. We persist it and tell Ordering to cancel the order.
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogWarning("❌ Payment FAILED for order {OrderId}: {Reason}", orderId, payment.FailureReason);
            await mediator.Publish(new PaymentFailedEvent(orderId, payment.FailureReason), CancellationToken.None);
        }
    }
}
