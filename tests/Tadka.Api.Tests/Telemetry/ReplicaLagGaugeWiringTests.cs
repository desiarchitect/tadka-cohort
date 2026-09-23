using System.Diagnostics.Metrics;
using Tadka.Telemetry;

namespace Tadka.Api.Tests.Telemetry;

// Proves the ACTUAL tadka.replica.lag_seconds ObservableGauge (ADR-063) — not just the pure math in
// ReplicaLagTests — reflects live application state, using System.Diagnostics.Metrics.MeterListener to
// observe the same in-process Meter the real app registers via AddTadkaTelemetry (no OTLP exporter, no
// Collector, no Docker needed; MeterListener works in-process regardless of whether an exporter is wired).
// Simulates MenuUpdatedConsumer's own state transitions directly: "stalled" = an old baseline sitting
// untouched; "resumed" = a fresh event just applied. This is the closest an automated test gets to the
// break-kit's manual "pause the consumer, watch it grow, resume, watch it shrink" demo.
public class ReplicaLagGaugeWiringTests
{
    [Fact]
    public void Gauge_reports_growing_lag_while_stalled_then_drops_after_a_fresh_apply()
    {
        double? observed = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == TadkaDiagnostics.MeterName && instrument.Name == "tadka.replica.lag_seconds")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => observed = measurement);
        listener.Start();

        // "Stalled": the last applied event was 10s ago and nothing new has arrived.
        var stalledBaseline = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref TadkaDiagnostics.LastMenuReplicaAppliedEventUnixMs, stalledBaseline);
        listener.RecordObservableInstruments();

        Assert.NotNull(observed);
        Assert.InRange(observed!.Value, 9.0, 20.0); // ~10s; generous window for test-runner scheduling jitter
        var lagWhileStalled = observed!.Value;

        // "Resumed": a fresh menu-updated event (timestamped ~now) gets applied, advancing the baseline —
        // exactly what MenuUpdatedConsumer's Interlocked.Exchange call does on a real message.
        observed = null;
        Interlocked.Exchange(ref TadkaDiagnostics.LastMenuReplicaAppliedEventUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        listener.RecordObservableInstruments();

        Assert.NotNull(observed);
        Assert.True(observed!.Value < lagWhileStalled, $"expected lag to drop after a fresh apply: was {lagWhileStalled}s, now {observed}s");
        Assert.InRange(observed!.Value, 0.0, 2.0);
    }
}
