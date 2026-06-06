using System.Diagnostics;
using Tadka.Telemetry;

namespace Tadka.Api.Tests.Telemetry;

// Unit tests for the heart of ADR-041: a W3C traceparent captured at enqueue (stored on the outbox row)
// must round-trip into a remote ActivityContext on the consumer, so the async Kafka hop rejoins the trace.
// No Kafka, no Docker — just the propagation contract.
public class TraceContextPropagationTests
{
    [Fact]
    public void TraceParent_round_trips_into_a_remote_context_with_matching_ids()
    {
        // Arrange: an activity (as if a request span were active when the outbox row was written).
        using var source = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("enqueue", ActivityKind.Internal)!;
        var captured = TadkaTrace.CurrentTraceParent();   // what we'd store on the outbox row

        // Act: parse it back on the "consumer" side.
        var parsed = TadkaTrace.ParseContext(captured);

        // Assert: same trace + span, and marked remote (a parent from another process).
        Assert.NotNull(captured);
        Assert.Equal(activity.TraceId, parsed.TraceId);
        Assert.Equal(activity.SpanId, parsed.SpanId);
        Assert.True(parsed.IsRemote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-traceparent")]
    public void Missing_or_invalid_traceparent_degrades_to_no_parent(string? bad)
    {
        // A forgotten/garbled header must NOT throw — it degrades to a new root span (default context).
        var parsed = TadkaTrace.ParseContext(bad);
        Assert.Equal(default, parsed);
    }
}
