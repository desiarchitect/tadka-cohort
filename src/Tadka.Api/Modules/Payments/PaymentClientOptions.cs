namespace Tadka.Api.Modules.Payments;

/// <summary>
/// Config for talking to the extracted Payment service (ADR-024/025). The gateway behaviour and the
/// "Synchronous" brownout lever moved OUT of the monolith on Day 8 — they belong to the Payment service
/// now. What's left here is the client's view: where the service lives, and the resilience knobs for the
/// network hop (the Day-7 timeout/bulkhead, reused around HTTP).
/// </summary>
public sealed class PaymentClientOptions
{
    public const string SectionName = "Payment";

    /// <summary><c>Async</c> (shipped): enqueue the charge for the background processor → <c>POST /orders</c>
    /// returns immediately. <c>Off</c>: payment is not triggered (feature flag; the test suite pins Day-4
    /// order semantics with it).</summary>
    public string Mode { get; set; } = "Async";

    /// <summary>Base URL of the Payment service (e.g. http://localhost:5240 in dev).</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Polly timeout (ADR-021/025) around the HTTP call to the Payment service.</summary>
    public double TimeoutSeconds { get; set; } = 2;

    /// <summary>Polly bulkhead permits (ADR-021/025) for concurrent calls to the Payment service.</summary>
    public int MaxConcurrentCharges { get; set; } = 10;

    public bool IsDisabled => string.Equals(Mode, "Off", StringComparison.OrdinalIgnoreCase);
}
