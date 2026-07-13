using Confluent.Kafka;
using Tadka.Payment.Api.Messaging;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Pure unit tests for the retry-then-DLQ decision (ADR-051) — no Kafka broker needed, matching the
/// project's "tests stay green without Redis/Kafka" rule. Confirms the exact bug this phase fixes: a
/// poison message used to retry forever and block the partition; now it is quarantined after N attempts.
/// </summary>
public class PoisonMessageTrackerTests
{
    private static TopicPartitionOffset Offset(long value = 42) =>
        new("order-placed", new Partition(0), new Offset(value));

    [Fact]
    public void First_failures_below_the_limit_do_not_route_to_the_DLQ()
    {
        var tracker = new PoisonMessageTracker(maxAttempts: 3);
        var offset = Offset();

        Assert.False(tracker.RecordFailureAndShouldDlq(offset)); // attempt 1
        Assert.False(tracker.RecordFailureAndShouldDlq(offset)); // attempt 2
    }

    [Fact]
    public void The_Nth_failure_routes_to_the_DLQ()
    {
        var tracker = new PoisonMessageTracker(maxAttempts: 3);
        var offset = Offset();

        tracker.RecordFailureAndShouldDlq(offset); // attempt 1
        tracker.RecordFailureAndShouldDlq(offset); // attempt 2
        Assert.True(tracker.RecordFailureAndShouldDlq(offset)); // attempt 3 — hits the limit
    }

    [Fact]
    public void Different_offsets_are_tracked_independently()
    {
        var tracker = new PoisonMessageTracker(maxAttempts: 2);
        var poisonOffset = Offset(1);
        var healthyOffset = Offset(2);

        tracker.RecordFailureAndShouldDlq(poisonOffset); // attempt 1 for the poison message

        // a different message at a different offset starts its own fresh count, not affected by the poison one
        Assert.False(tracker.RecordFailureAndShouldDlq(healthyOffset));
    }

    [Fact]
    public void Clear_resets_the_count_so_the_offset_gets_a_fresh_budget()
    {
        var tracker = new PoisonMessageTracker(maxAttempts: 2);
        var offset = Offset();

        tracker.RecordFailureAndShouldDlq(offset); // attempt 1

        tracker.Clear(offset); // e.g. after a successful reprocess, or after DLQ routing

        Assert.False(tracker.RecordFailureAndShouldDlq(offset)); // fresh attempt 1, not attempt 2
    }

    [Fact]
    public void Without_a_bound_a_poison_message_would_retry_forever_this_is_exactly_what_MaxAttempts_prevents()
    {
        var tracker = new PoisonMessageTracker(maxAttempts: 3);
        var offset = Offset();

        var dlqRoutedOnAttempt = 0;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            if (tracker.RecordFailureAndShouldDlq(offset))
            {
                dlqRoutedOnAttempt = attempt;
                break;
            }
        }

        Assert.Equal(3, dlqRoutedOnAttempt); // quarantined at the configured limit, not attempt 10
    }
}
