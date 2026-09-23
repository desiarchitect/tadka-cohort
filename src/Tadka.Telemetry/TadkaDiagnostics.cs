using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tadka.Telemetry;

/// <summary>
/// The single shared <see cref="ActivitySource"/> + <see cref="Meter"/> for all Tadka services (ADR-040).
/// Every service registers these names (<c>AddSource("Tadka")</c> / <c>AddMeter("Tadka")</c>) so custom
/// spans and metrics flow through the same OTEL pipeline.
///
/// CARDINALITY DISCIPLINE (ADR-042): the metrics here carry only LOW-cardinality labels (e.g. payment
/// status = success|failed). Per-entity ids (order_id, user_id) are NEVER metric labels — they go on
/// trace span attributes (where high cardinality is cheap) and structured logs. A single order_id label
/// on a metric = one Prometheus series per order = TSDB blow-up.
/// </summary>
public static class TadkaDiagnostics
{
    public const string ActivitySourceName = "Tadka";
    public const string MeterName = "Tadka";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Orders placed (business throughput). No labels — a plain counter.</summary>
    public static readonly Counter<long> OrdersPlaced =
        Meter.CreateCounter<long>("tadka.orders.placed", unit: "{order}", description: "Orders accepted by Ordering.");

    /// <summary>Payment outcomes, labelled ONLY by status (success|failed) — low cardinality (ADR-042).</summary>
    public static readonly Counter<long> PaymentResults =
        Meter.CreateCounter<long>("tadka.payment.result", unit: "{payment}", description: "Payment results by status.");

    /// <summary>Payment amounts (₹) as a histogram — distribution, not per-order series.</summary>
    public static readonly Histogram<double> PaymentAmount =
        Meter.CreateHistogram<double>("tadka.payment.amount", unit: "INR", description: "Charged amount distribution.");

    /// <summary>Circuit-breaker state transitions on the Payment→gateway pipeline (ADR-043), labelled by
    /// <c>state</c> = open|closed|half_open (low cardinality). Lets a Grafana panel show the breaker trip + recover.</summary>
    public static readonly Counter<long> PaymentCircuitTransitions =
        Meter.CreateCounter<long>("tadka.payment.circuit_transitions", description: "Payment gateway circuit-breaker state transitions.");

    /// <summary>DEMO-ONLY anti-pattern (ADR-042): a counter that the OrdersController labels with order_id when
    /// <c>OTEL_CARDINALITY_DEMO=true</c>. Each order becomes a brand-new Prometheus series → watch the series
    /// count explode, then revert. This exists ONLY to show why ids must never be metric labels.</summary>
    public static readonly Counter<long> OrdersPlacedByIdBAD =
        Meter.CreateCounter<long>("tadka.orders.placed.by_id", description: "DEMO ONLY — high-cardinality anti-pattern (ADR-042). Do not copy.");

    /// <summary>
    /// Unix-ms timestamp (Kafka's own per-message timestamp, i.e. when the <c>menu-updated</c> event was
    /// produced) of the most recent event <see cref="MenuUpdatedConsumer"/> actually applied to the local
    /// replica (ADR-063). Starts at process-start time so the gauge reads ~0 before the first event ever
    /// arrives rather than an undefined/huge lag. A plain static field, not per-request state — one
    /// consumer instance per process (ADR-042 cardinality discipline: one series, no labels).
    /// </summary>
    public static long LastMenuReplicaAppliedEventUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Pure calculation backing <see cref="ReplicaLagSeconds"/>, pulled out so it's testable without
    /// a live OTEL pipeline or a real clock (ADR-063).</summary>
    public static double ComputeReplicaLagSeconds(long lastAppliedEventUnixMs, long? nowUnixMs = null)
        => Math.Max(0, ((nowUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - lastAppliedEventUnixMs) / 1000.0);

    /// <summary>
    /// Replica staleness (ADR-063): seconds since the local menu/price replica last applied a
    /// <c>menu-updated</c> event, evaluated live at scrape time — not a per-message histogram. That's
    /// deliberate: if the Outbox relay or this consumer stalls, NO new messages arrive to record a sample,
    /// so a per-message metric would just go flat and hide the stall. An observed gauge keeps counting up
    /// from the last known-applied event even while nothing is happening, which is the actual "are we
    /// silently serving stale prices right now" signal ADR-037 never had.
    /// </summary>
    public static readonly ObservableGauge<double> ReplicaLagSeconds = Meter.CreateObservableGauge(
        "tadka.replica.lag_seconds",
        () => ComputeReplicaLagSeconds(Interlocked.Read(ref LastMenuReplicaAppliedEventUnixMs)),
        unit: "s",
        description: "Seconds since the local menu/price replica last applied a menu-updated event.");
}
