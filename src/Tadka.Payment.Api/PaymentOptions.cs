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

    /// <summary>
    /// DEMO LEVER (Day 8 crash/fault isolation): when true, a charge calls <c>Environment.FailFast</c> and
    /// the Payment service process dies. Post-extraction this kills ONLY this service — the monolith keeps
    /// serving menus and accepting orders. (In-process, on Day 7, the same fatal would have taken the whole
    /// host down.) Never set in production.
    /// </summary>
    public bool CrashOnCharge { get; set; }

    /// <summary>
    /// DEMO LEVER (Day 10, ADR-046): when true, deliberately logs the raw card number as a developer
    /// "just for debugging" mistake would — the exact anti-pattern tokenization exists to prevent.
    /// Default false (the correct behaviour ships by default; flip this on only to show the break).
    /// </summary>
    public bool LogRawCardNumber { get; set; }

    public GatewayOptions Gateway { get; set; } = new();
}

public sealed class GatewayOptions
{
    /// <summary><c>Fast</c> (~200 ms) · <c>Slow</c> (a provider incident, ~8 s) · <c>Failing</c> (declines).</summary>
    public string Behavior { get; set; } = "Fast";
    public double SlowDelaySeconds { get; set; } = 8;
    public double FastDelayMs { get; set; } = 200;
}
