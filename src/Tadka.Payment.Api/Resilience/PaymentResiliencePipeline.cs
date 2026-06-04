using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;

namespace Tadka.Payment.Api.Resilience;

/// <summary>
/// The Polly v8 pipeline wrapping every external gateway call (ADR-021) — moved here from the monolith
/// at extraction (the pattern travelled with the dependency it protects). Two strategies:
/// <list type="bullet">
/// <item><b>Concurrency limiter (bulkhead, outermost):</b> at most <c>MaxConcurrentCharges</c> in flight;
/// the rest are rejected immediately (<see cref="RateLimiterRejectedException"/>) — a sick gateway can't
/// drain the pool.</item>
/// <item><b>Timeout (innermost):</b> a charge past the deadline is abandoned
/// (<see cref="Polly.Timeout.TimeoutRejectedException"/>) — fail fast, not an 8 s hold.</item>
/// </list>
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
                queueLimit: 0)
            .AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds <= 0 ? 2 : o.TimeoutSeconds))
            .Build();
    }
}
