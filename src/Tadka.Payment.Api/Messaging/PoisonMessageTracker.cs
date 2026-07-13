using Confluent.Kafka;

namespace Tadka.Payment.Api.Messaging;

/// <summary>Tracks consecutive processing failures per (topic, partition, offset) so a poison message is
/// retried a bounded number of times before being routed to the DLQ (ADR-051), instead of blocking the
/// partition forever. Process-local: a consumer restart resets the count, so a message that failed twice
/// before a restart gets <see cref="MaxAttempts"/> fresh tries again - an honest limitation for a
/// single-process teaching deploy, not a hidden bug (the alternative, persisting attempt counts, is real
/// production hardening out of scope here).</summary>
public sealed class PoisonMessageTracker
{
    private readonly Dictionary<TopicPartitionOffset, int> _attempts = new();

    public PoisonMessageTracker(int maxAttempts = 3) => MaxAttempts = maxAttempts;

    public int MaxAttempts { get; }

    /// <summary>Records one more failure for this offset. Returns true once attempts have reached the
    /// limit and the caller should route the message to the DLQ and commit past it.</summary>
    public bool RecordFailureAndShouldDlq(TopicPartitionOffset offset)
    {
        var attempts = _attempts.TryGetValue(offset, out var n) ? n + 1 : 1;
        _attempts[offset] = attempts;
        return attempts >= MaxAttempts;
    }

    /// <summary>Forgets this offset — call after a successful process or after routing to the DLQ.</summary>
    public void Clear(TopicPartitionOffset offset) => _attempts.Remove(offset);
}
