using Microsoft.Extensions.Options;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// A stand-in for a real payment provider whose behaviour we can dial from config — the instrument
/// that lets the cohort *cause* the brownout on demand. <c>Fast</c> ≈ a healthy provider; <c>Slow</c>
/// ≈ a provider having an incident (8 s); <c>Failing</c> ≈ a decline. The delays honour the
/// cancellation token, so Polly's timeout (ADR-021) can actually abandon a slow charge.
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

            default: // "fast" / healthy
                await Task.Delay(TimeSpan.FromMilliseconds(g.FastDelayMs), cancellationToken);
                break;
        }

        return $"FAKEPAY-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
    }
}
