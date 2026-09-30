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

public sealed class GatewayOptions
{
    /// <summary><c>Fast</c> (~200 ms) - <c>Slow</c> (a provider incident, ~8 s) - <c>Failing</c> (declines, a
    /// final business answer, never retried) - <c>Outage</c> (transport failures, retried, deterministically
    /// trips the circuit breaker - ADR-039/043; see scripts/inject-incident.ps1 -Scenario payment-outage).</summary>
    public string Behavior { get; set; } = "Fast";
    public double SlowDelaySeconds { get; set; } = 8;
    public double FastDelayMs { get; set; } = 200;
}
