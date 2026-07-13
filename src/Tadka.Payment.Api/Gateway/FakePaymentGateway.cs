using Microsoft.Extensions.Options;
using Tadka.Payment.Api.Domain;

namespace Tadka.Payment.Api.Gateway;

/// <summary>
/// A stand-in for a real payment provider whose behaviour we dial from config — the instrument that lets
/// the cohort *cause* a slow/declining/erroring provider on demand. <c>Fast</c> ≈ healthy; <c>Slow</c> ≈ an
/// incident (8 s, → timeout); <c>Failing</c> ≈ a business <b>decline</b> (final, no retry/breaker); <c>Outage</c>
/// ≈ the gateway is <b>down/erroring</b> (transport failure → retried + trips the circuit breaker, ADR-043).
/// Delays honour the cancellation token so Polly's timeout (ADR-021) can abandon a slow charge.
/// </summary>
public sealed class FakePaymentGateway(IOptionsMonitor<PaymentOptions> options, ILogger<FakePaymentGateway> logger)
    : IPaymentGateway
{
    public async Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken)
    {
        var g = options.CurrentValue.Gateway;
        var behavior = (g.Behavior ?? "Fast").Trim().ToLowerInvariant();

        switch (behavior)
        {
            case "slow":
                logger.LogWarning(
                    "💤 FakeGateway SLOW: charging order {OrderId} will take ~{Seconds}s (simulated provider incident).",
                    orderId, g.SlowDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(g.SlowDelaySeconds), cancellationToken);
                break;

            case "failing":
                await Task.Delay(TimeSpan.FromMilliseconds(g.FastDelayMs), cancellationToken);
                throw new PaymentDeclinedException("Gateway declined the payment (simulated).");

            case "outage":
                // The provider is down/erroring (5xx). A TRANSPORT failure — retried, and counts toward the
                // circuit breaker (ADR-043). Contrast with "failing" (a business decline, which does neither).
                await Task.Delay(TimeSpan.FromMilliseconds(g.FastDelayMs), cancellationToken);
                throw new PaymentGatewayUnavailableException("Gateway unavailable / 5xx (simulated outage).");

            default: // "fast" / healthy
                await Task.Delay(TimeSpan.FromMilliseconds(g.FastDelayMs), cancellationToken);
                break;
        }

        return $"FAKEPAY-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
    }

    public async Task<string> RefundAsync(
        Guid orderId, string? originalGatewayReference, Money amount, CancellationToken cancellationToken)
    {
        var g = options.CurrentValue.Gateway;
        // Refunds stay Fast by default — the compensation path must be reliable for the demo.
        // (A failing refund is a separate reconciliation problem, not the Day-11 headline.)
        await Task.Delay(TimeSpan.FromMilliseconds(g.FastDelayMs), cancellationToken);
        var refCode = $"FAKEREF-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        logger.LogInformation(
            "💸 FakeGateway REFUND for order {OrderId} (original {Original}) → {RefundRef}.",
            orderId, originalGatewayReference ?? "(none)", refCode);
        return refCode;
    }
}
