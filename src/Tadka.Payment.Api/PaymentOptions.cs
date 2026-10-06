namespace Tadka.Payment.Api;

/// <summary>
/// The Payment service's config (bound from the "Payment" section). The gateway behaviour + resilience
/// knobs that used to live in the monolith (Day 7) now belong here — this service owns the dependency.
/// </summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    /// <summary>Polly timeout (ADR-021) around the gateway call. High (e.g. 30) ≈ "no timeout".</summary>
    public double TimeoutSeconds { get; set; } = 2;

    /// <summary>Polly bulkhead permits (ADR-021). High (e.g. 1000) ≈ "no bulkhead".</summary>
    public int MaxConcurrentCharges { get; set; } = 10;

    // ── Circuit breaker + retry (ADR-043) ────────────────────────────────────────────────────────────
    /// <summary>Retry attempts on a TRANSPORT failure (timeout / gateway-unavailable), jittered exponential
    /// backoff. 0 ≈ no retry. Never retries a business decline.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Circuit opens when this fraction of calls fail within the sampling window (min-throughput met).</summary>
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>Minimum calls in the sampling window before the breaker can open (stops one blip tripping it).</summary>
    public int CircuitMinimumThroughput { get; set; } = 5;

    /// <summary>Rolling window over which the failure ratio is measured (seconds).</summary>
    public double CircuitSamplingSeconds { get; set; } = 30;

    /// <summary>How long the circuit stays open (fail-fast) before a half-open probe (seconds).</summary>
    public double CircuitBreakSeconds { get; set; } = 60;

    /// <summary>
    /// Fix 2 / ADR-043 lever: what to do with an order when the gateway is unavailable (open circuit,
    /// transport failure, or timeout) - as opposed to a business decline, which is always Compensate.
    /// <c>Compensate</c> (default): today's demo behaviour - save Failed immediately, the caller
    /// (Ordering) cancels the order. Fast feedback, but permanent - the order is never retried even
    /// after the gateway recovers. <c>Buffer</c>: treat it as "not attempted" - leave no Payment row,
    /// throw so the Kafka consumer seeks back and redelivers instead of committing, and confirm the
    /// order later once the gateway is healthy again. See ADR-043's trade-off note.
    /// </summary>
    public GatewayUnavailableMode OnGatewayUnavailable { get; set; } = GatewayUnavailableMode.Compensate;

    /// <summary>
    /// DEMO LEVER (Day 8 crash/fault isolation): when true, a charge calls <c>Environment.FailFast</c> and
    /// the Payment service process dies. Post-extraction this kills ONLY this service — the monolith keeps
    /// serving menus and accepting orders. (In-process, on Day 7, the same fatal would have taken the whole
    /// host down.) Never set in production.
    /// </summary>
    public bool CrashOnCharge { get; set; }

    /// <summary>
    /// DEMO LEVER (Day 10, ADR-053): when true, deliberately logs the raw card number as a developer
    /// "just for debugging" mistake would — the exact anti-pattern tokenization exists to prevent.
    /// Default false (the correct behaviour ships by default; flip this on only to show the break).
    /// </summary>
    public bool LogRawCardNumber { get; set; }

    public GatewayOptions Gateway { get; set; } = new();
}

/// <summary>Fix 2 / ADR-043: what PaymentService.ChargeAsync does when the gateway itself is unreachable.</summary>
public enum GatewayUnavailableMode
{
    /// <summary>Save Failed immediately (today's default demo behaviour) - fast, but permanent.</summary>
    Compensate,

    /// <summary>Leave no Payment row and throw for redelivery instead - the order confirms later.</summary>
    Buffer
}

public sealed class GatewayOptions
{
    /// <summary><c>Fast</c> (~200 ms) - <c>Slow</c> (a provider incident, ~8 s) - <c>Failing</c> (declines, a
    /// final business answer, never retried) - <c>Outage</c> (transport failures, retried, deterministically
    /// trips the circuit breaker - ADR-039/043; see scripts/inject-incident.ps1 -Scenario payment-outage).</summary>
    public string Behavior { get; set; } = "Fast";
    public double SlowDelaySeconds { get; set; } = 8;
    public double FastDelayMs { get; set; } = 200;
}
