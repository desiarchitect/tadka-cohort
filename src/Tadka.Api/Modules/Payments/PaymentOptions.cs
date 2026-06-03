namespace Tadka.Api.Modules.Payments;

/// <summary>
/// The Payment module's config (bound from the "Payment" section). Every knob here is a teaching lever
/// for the Day-7 break-kit — there is no other reason a real system would expose a "Synchronous" mode.
/// </summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    /// <summary>
    /// <c>Async</c> (shipped default, ADR-023): payment runs OFF the request path via the background
    /// processor — <c>POST /orders</c> returns immediately. <c>Synchronous</c>: the naive baseline —
    /// payment runs INSIDE the order-creation request. Flip to <c>Synchronous</c> + a slow gateway to
    /// reproduce the brownout. <c>Off</c>: payment is not triggered at all (a feature flag; the test
    /// suite uses it to pin Day-4 order semantics — payment has its own dedicated tests).
    /// </summary>
    public string Mode { get; set; } = "Async";

    /// <summary>Polly timeout (ADR-021). Set high (e.g. 30) to simulate "no timeout" for the baseline.</summary>
    public double TimeoutSeconds { get; set; } = 2;

    /// <summary>Polly bulkhead permits (ADR-021). Set high (e.g. 1000) to simulate "no bulkhead".</summary>
    public int MaxConcurrentCharges { get; set; } = 10;

    public GatewayOptions Gateway { get; set; } = new();

    public bool IsSynchronous => string.Equals(Mode, "Synchronous", StringComparison.OrdinalIgnoreCase);

    public bool IsDisabled => string.Equals(Mode, "Off", StringComparison.OrdinalIgnoreCase);
}

public sealed class GatewayOptions
{
    /// <summary><c>Fast</c> (~200 ms) · <c>Slow</c> (the brownout, ~8 s) · <c>Failing</c> (declines).</summary>
    public string Behavior { get; set; } = "Fast";
    public double SlowDelaySeconds { get; set; } = 8;
    public double FastDelayMs { get; set; } = 200;
}
