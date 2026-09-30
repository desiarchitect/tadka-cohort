using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;
using Tadka.Payment.Api.Gateway;
using Tadka.Payment.Api.Infrastructure;
using Tadka.Payment.Api.Resilience;
using Tadka.Telemetry;

namespace Tadka.Payment.Api;

/// <summary>The outcome of a charge attempt, returned to the HTTP caller (the monolith).</summary>
public sealed record ChargeOutcome(PaymentStatus Status, string? GatewayReference, string? FailureReason);

/// <summary>The outcome of a refund attempt (ADR-045).</summary>
public sealed record RefundOutcome(PaymentStatus Status, bool Found);

/// <summary>
/// Charges an order through the Polly-wrapped gateway and records the outcome (ADR-021). This is the same
/// logic that lived in the monolith on Day 7 — it MOVED here at extraction (ADR-024); only the caller
/// changed (in-process MediatR handler → an HTTP request). Idempotent: one payment per order (unique
/// index), so a redelivered charge returns the existing outcome instead of charging again.
/// </summary>
public sealed class PaymentService(
    PaymentDbContext db,
    IPaymentGateway gateway,
    PaymentResiliencePipeline resilience,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<PaymentService> logger)
{
    public async Task<ChargeOutcome> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken = default, string? cardNumber = null, Guid? customerId = null)
    {
        // Custom business span (ADR-040): auto-instrumentation gives HTTP/DB spans, but "how long does the
        // charge take?" is a business question only a custom span answers. order.id + amount go on the SPAN
        // (high cardinality is fine here) — NEVER as metric labels (ADR-042).
        using var activity = TadkaDiagnostics.ActivitySource.StartActivity("ProcessPayment", ActivityKind.Internal);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("payment.amount", amount.Amount);

        // DEMO LEVER (Day 8): a fatal in the charge path. Post-extraction this kills ONLY this service.
        if (options.CurrentValue.CrashOnCharge)
        {
            logger.LogCritical("💥 CrashOnCharge lever set — failing fast to demonstrate fault isolation (Day 8).");
            Environment.FailFast("Payment service crash demo (Day 8) — this must NOT take the monolith down.");
        }

        // Idempotency: if a payment already exists for this order, return its outcome (don't charge twice).
        var existing = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Payment for order {OrderId} already exists ({Status}); returning it (idempotent).", orderId, existing.Status);
            return new ChargeOutcome(existing.Status, existing.GatewayReference, existing.FailureReason);
        }

        // Tokenize (ADR-053) the moment the card arrives — cardNumber itself is never logged or stored
        // beyond this point; only the token and last 4 digits survive past this line.
        string? cardToken = null, cardLast4 = null;
        if (!string.IsNullOrWhiteSpace(cardNumber))
        {
            (cardToken, cardLast4) = CardTokenizer.Tokenize(cardNumber);
            if (options.CurrentValue.LogRawCardNumber)
                logger.LogWarning("DEMO LEVER (LogRawCardNumber): raw card number {CardNumber} for order {OrderId} — this must NEVER happen in real code.", cardNumber, orderId);
        }

        var payment = new Domain.Payment
        {
            OrderId = orderId,
            CustomerId = customerId, // resource ownership (ADR-031) — GET /payments/{orderId} checks this
            Amount = amount,
            Method = "UPI",
            Status = PaymentStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            CardToken = cardToken,
            CardLast4 = cardLast4
        };
        db.Payments.Add(payment);

        try
        {
            await db.SaveChangesAsync(cancellationToken); // a Pending row exists before we call the gateway
        }
        catch (DbUpdateException)
        {
            // Lost the unique-index race with a concurrent charge for the same order → idempotent: re-read.
            db.Entry(payment).State = EntityState.Detached;
            var winner = await db.Payments.AsNoTracking().FirstAsync(p => p.OrderId == orderId, cancellationToken);
            return new ChargeOutcome(winner.Status, winner.GatewayReference, winner.FailureReason);
        }

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

            // Low-cardinality business metrics (ADR-042): status is bounded; amount is a histogram.
            TadkaDiagnostics.PaymentResults.Add(1, new KeyValuePair<string, object?>("status", "success"));
            TadkaDiagnostics.PaymentAmount.Record((double)amount.Amount);
            activity?.SetTag("payment.status", "success");

            logger.LogInformation("💳 Payment COMPLETED for order {OrderId} (ref {Reference}).", orderId, reference);
            return new ChargeOutcome(PaymentStatus.Completed, reference, null);
        }
        catch (Exception ex)
        {
            // Timeout (slow gateway), bulkhead rejection, or a decline — all land here as a fast, contained
            // BUSINESS outcome (HTTP 200 with Failed). It is NOT a transport failure: the caller can cancel
            // the order. (A DOWN service is a different thing — the caller's HTTP call throws.)
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);

            TadkaDiagnostics.PaymentResults.Add(1, new KeyValuePair<string, object?>("status", "failed"));
            activity?.SetTag("payment.status", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, payment.FailureReason);

            logger.LogWarning("❌ Payment FAILED for order {OrderId}: {Reason}", orderId, payment.FailureReason);
            return new ChargeOutcome(PaymentStatus.Failed, null, payment.FailureReason);
        }
    }

    /// <summary>
    /// Compensating refund after a restaurant rejects an already-paid order (ADR-045).
    /// Idempotent: a second refund request for the same order returns the existing Refunded row.
    /// </summary>
    public async Task<RefundOutcome> RefundAsync(Guid orderId, string? gatewayReference, CancellationToken cancellationToken = default)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);
        if (payment is null)
        {
            logger.LogWarning("Refund requested for order {OrderId} but no payment row exists.", orderId);
            return new RefundOutcome(PaymentStatus.Failed, Found: false);
        }

        if (payment.Status == PaymentStatus.Refunded)
        {
            logger.LogInformation("Payment for order {OrderId} already Refunded — idempotent return.", orderId);
            return new RefundOutcome(PaymentStatus.Refunded, Found: true);
        }

        if (payment.Status != PaymentStatus.Completed)
        {
            logger.LogWarning(
                "Refund requested for order {OrderId} but payment is {Status} (only Completed can refund).",
                orderId, payment.Status);
            return new RefundOutcome(payment.Status, Found: true);
        }

        var refundRef = await gateway.RefundAsync(
            orderId,
            gatewayReference ?? payment.GatewayReference,
            payment.Amount,
            cancellationToken);

        payment.Status = PaymentStatus.Refunded;
        payment.GatewayReference = refundRef;
        payment.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("💸 Payment REFUNDED for order {OrderId} (ref {Reference}).", orderId, refundRef);
        return new RefundOutcome(PaymentStatus.Refunded, Found: true);
    }
}
