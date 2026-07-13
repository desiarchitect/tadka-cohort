namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// Day 6, Beat (SSE reconnect replay): an <see cref="OrderTrackingEvent"/> plus a per-order,
/// monotonically increasing sequence number, used both as the SSE frame's `id:` field and as the
/// key for replaying missed events after a reconnect (`Last-Event-ID`). This buffer is a bounded
/// recent-history window, not a durable log — the limit is explicit in ADR-020's teaching: this
/// is what a buffer can do; true no-loss delivery is the outbox pattern, Week 5.
/// </summary>
public sealed record SequencedTrackingEvent(long Seq, OrderTrackingEvent Event);
