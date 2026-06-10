using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Tadka.Payment.Api.Gateway;
using Tadka.Telemetry;

namespace Tadka.Payment.Api.Resilience;

/// <summary>
/// The Polly v8 pipeline wrapping every external gateway call. Completed over two days:
/// <list type="bullet">
/// <item><b>Bulkhead (concurrency limiter, outermost, ADR-021):</b> at most <c>MaxConcurrentCharges</c> in
/// flight; the rest are rejected immediately — a sick gateway can't drain the pool.</item>
/// <item><b>Retry (ADR-043):</b> jittered exponential backoff on <i>transport</i> failures only
/// (timeout / gateway-unavailable) — a transient blip is re-tried; a business decline is never re-tried.</item>
/// <item><b>Circuit breaker (ADR-043):</b> opens on sustained transport failure (ratio + minimum throughput)
/// → fail fast for the break duration, then a half-open probe. Declines do NOT count toward it.</item>
/// <item><b>Timeout (innermost, ADR-021):</b> a charge past the deadline is abandoned
/// (<see cref="TimeoutRejectedException"/>, a transport failure) — fail fast, not an 8 s hold.</item>
/// </list>
/// Canonical Polly order (outer→inner): bulkhead → retry → circuit breaker → per-attempt timeout.
/// Circuit-breaker state transitions are emitted as a metric (ADR-040/043) so the trip + recovery are
/// visible on a Grafana panel.
/// </summary>
public sealed class PaymentResiliencePipeline
{
    public ResiliencePipeline Pipeline { get; }

    public PaymentResiliencePipeline(IOptions<PaymentOptions> options, ILogger<PaymentResiliencePipeline> logger)
    {
        var o = options.Value;

        // Only TRANSPORT failures are retried + counted by the breaker (ADR-043). A business decline
        // (PaymentDeclinedException) is final — it flows straight through to a Failed outcome.
        static bool IsTransport(Exception? ex) =>
            ex is TimeoutRejectedException or PaymentGatewayUnavailableException;

        var builder = new ResiliencePipelineBuilder();

        // 1. Bulkhead (outermost).
        builder.AddConcurrencyLimiter(
            permitLimit: Math.Max(1, o.MaxConcurrentCharges),
            queueLimit: 0);

        // 2. Retry — transient transport failures only, jittered backoff (no thundering herd).
        if (o.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => new ValueTask<bool>(IsTransport(args.Outcome.Exception)),
                MaxRetryAttempts = o.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
            });
        }

        // 3. Circuit breaker — open on sustained transport failure, fail fast, give the gateway room.
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = args => new ValueTask<bool>(IsTransport(args.Outcome.Exception)),
            FailureRatio = o.CircuitFailureRatio is <= 0 or > 1 ? 0.5 : o.CircuitFailureRatio,
            MinimumThroughput = Math.Max(2, o.CircuitMinimumThroughput),
            SamplingDuration = TimeSpan.FromSeconds(o.CircuitSamplingSeconds < 1 ? 30 : o.CircuitSamplingSeconds),
            BreakDuration = TimeSpan.FromSeconds(o.CircuitBreakSeconds <= 0 ? 60 : o.CircuitBreakSeconds),
            OnOpened = args =>
            {
                TadkaDiagnostics.PaymentCircuitTransitions.Add(1, new KeyValuePair<string, object?>("state", "open"));
                logger.LogError("⚡ Payment circuit OPENED for ~{Break}s — gateway looks down; failing fast.",
                    args.BreakDuration.TotalSeconds);
                return default;
            },
            OnClosed = _ =>
            {
                TadkaDiagnostics.PaymentCircuitTransitions.Add(1, new KeyValuePair<string, object?>("state", "closed"));
                logger.LogInformation("✅ Payment circuit CLOSED — gateway healthy again.");
                return default;
            },
            OnHalfOpened = _ =>
            {
                TadkaDiagnostics.PaymentCircuitTransitions.Add(1, new KeyValuePair<string, object?>("state", "half_open"));
                logger.LogInformation("🔁 Payment circuit HALF-OPEN — probing the gateway with one request.");
                return default;
            },
        });

        // 4. Per-attempt timeout (innermost).
        builder.AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds <= 0 ? 2 : o.TimeoutSeconds));

        Pipeline = builder.Build();
    }
}
