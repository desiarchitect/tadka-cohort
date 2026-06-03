using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;
using Tadka.Api.Modules.Payments;

namespace Tadka.Api.Infrastructure.Resilience;

/// <summary>
/// The Polly v8 pipeline that wraps every external payment-gateway call (ADR-021). Two strategies:
/// <list type="bullet">
/// <item><b>Concurrency limiter (bulkhead, outermost):</b> at most <c>MaxConcurrentCharges</c> charges
/// in flight; the rest are rejected immediately (<see cref="RateLimiterRejectedException"/>). A sick
/// gateway can never consume more than N slots — it can't drain the whole pool.</item>
/// <item><b>Timeout (innermost):</b> a charge that hasn't returned by the deadline is abandoned
/// (<see cref="Polly.Timeout.TimeoutRejectedException"/>) — fail fast instead of holding resources for 8 s.</item>
/// </list>
/// Set <c>TimeoutSeconds</c> high + <c>MaxConcurrentCharges</c> high in config to demonstrate the
/// "no resilience" baseline; the shipped defaults (2 s / 10) are the fix.
/// </summary>
public sealed class PaymentResiliencePipeline
{
    public ResiliencePipeline Pipeline { get; }

    public PaymentResiliencePipeline(IOptions<PaymentOptions> options)
    {
        var o = options.Value;

        Pipeline = new ResiliencePipelineBuilder()
            .AddConcurrencyLimiter(
                permitLimit: Math.Max(1, o.MaxConcurrentCharges),
                queueLimit: 0) // no queue: over the cap → reject now (back-pressure), don't pile up
            .AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds <= 0 ? 2 : o.TimeoutSeconds))
            .Build();
    }
}
