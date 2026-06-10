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
}
