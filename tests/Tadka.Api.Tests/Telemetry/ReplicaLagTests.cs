using Tadka.Telemetry;

namespace Tadka.Api.Tests.Telemetry;

// Unit tests for the pure lag calculation backing ADR-063's tadka.replica.lag_seconds gauge. No Kafka,
// no Docker, no real clock — just the arithmetic, mirroring TraceContextPropagationTests' pattern for
// ADR-041. The gauge itself (ObservableGauge, scraped by OTEL) isn't easily unit-testable without a real
// MeterListener/OTEL pipeline; the calculation it wraps is, and is where a bug would actually hide.
public class ReplicaLagTests
{
    [Fact]
    public void Lag_is_zero_right_after_an_event_is_applied()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastApplied = now; // just applied "now"

        var lag = TadkaDiagnostics.ComputeReplicaLagSeconds(lastApplied, now);

        Assert.Equal(0, lag);
    }

    [Fact]
    public void Lag_grows_the_longer_the_consumer_stays_stalled()
    {
        var lastApplied = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fiveSecondsLater = lastApplied + 5_000;
        var thirtySecondsLater = lastApplied + 30_000;

        var lagAt5s = TadkaDiagnostics.ComputeReplicaLagSeconds(lastApplied, fiveSecondsLater);
        var lagAt30s = TadkaDiagnostics.ComputeReplicaLagSeconds(lastApplied, thirtySecondsLater);

        Assert.Equal(5, lagAt5s);
        Assert.Equal(30, lagAt30s);
        Assert.True(lagAt30s > lagAt5s); // the "observe it grow while paused" behaviour, without a real clock/consumer
    }

    [Fact]
    public void Lag_shrinks_back_down_when_a_fresh_event_is_applied()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleBaseline = now - 60_000; // the consumer was stalled for a minute

        var lagWhileStalled = TadkaDiagnostics.ComputeReplicaLagSeconds(staleBaseline, now);
        Assert.Equal(60, lagWhileStalled);

        // Resume: a fresh event (timestamped ~now) gets applied, advancing the baseline.
        var freshEventTimestamp = now - 200; // a small, realistic publish-to-consume delay
        var lagAfterResume = TadkaDiagnostics.ComputeReplicaLagSeconds(freshEventTimestamp, now);

        Assert.True(lagAfterResume < lagWhileStalled);
        Assert.Equal(0.2, lagAfterResume, precision: 3);
    }

    [Fact]
    public void Lag_never_goes_negative_even_with_clock_skew()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastApplied = now + 500; // a slightly-ahead event timestamp (clock skew) shouldn't yield negative lag

        var lag = TadkaDiagnostics.ComputeReplicaLagSeconds(lastApplied, now);

        Assert.Equal(0, lag);
    }
}
