using System.Text.Json;
using StackExchange.Redis;

namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// Redis pub/sub implementation of the live-tracking backplane (ADR-020). Publishing and subscribing
/// both go through Redis channel <c>order:{id}</c>, so the publishing instance and the
/// connection-holding instance need not be the same box — the backplane bridges them.
///
/// Also maintains a short, capped recent-events buffer per order (a Redis LIST, last 20 events,
/// 6h TTL) so a reconnecting SSE client can replay what it missed (Day 6, `Last-Event-ID`).
/// </summary>
public sealed class RedisOrderTrackingBus(IConnectionMultiplexer redis) : IOrderTrackingBus
{
    private readonly IConnectionMultiplexer _redis = redis;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int RecentEventsCapacity = 20;
    private static readonly TimeSpan BufferTtl = TimeSpan.FromHours(6);

    public bool IsEnabled => true;

    private static RedisChannel Channel(Guid orderId) => RedisChannel.Literal($"order:{orderId}");
    private static RedisKey SeqKey(Guid orderId) => (RedisKey)$"order:{orderId}:seq";
    private static RedisKey RecentKey(Guid orderId) => (RedisKey)$"order:{orderId}:recent";

    public async Task PublishAsync(OrderTrackingEvent trackingEvent, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();

        // The sequence + recent-events buffer make replay possible; the pub/sub publish is what
        // wakes up anyone currently connected. Order matters: buffer first, so a client that
        // reconnects immediately after this publish can never see a seq in the live stream that
        // isn't already in the buffer it would replay from.
        var seq = await db.StringIncrementAsync(SeqKey(trackingEvent.OrderId));
        await db.KeyExpireAsync(SeqKey(trackingEvent.OrderId), BufferTtl);

        var sequenced = new SequencedTrackingEvent(seq, trackingEvent);
        var payload = JsonSerializer.Serialize(sequenced, Json);

        await db.ListRightPushAsync(RecentKey(trackingEvent.OrderId), payload);
        await db.ListTrimAsync(RecentKey(trackingEvent.OrderId), -RecentEventsCapacity, -1);
        await db.KeyExpireAsync(RecentKey(trackingEvent.OrderId), BufferTtl);

        await _redis.GetSubscriber().PublishAsync(Channel(trackingEvent.OrderId), payload);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(Guid orderId, Func<SequencedTrackingEvent, Task> onEvent, CancellationToken ct = default)
    {
        var subscriber = _redis.GetSubscriber();
        var channel = Channel(orderId);

        await subscriber.SubscribeAsync(channel, async (_, message) =>
        {
            if (!message.HasValue) return;
            var sequenced = JsonSerializer.Deserialize<SequencedTrackingEvent>((string)message!, Json);
            if (sequenced is not null) await onEvent(sequenced);
        });

        return new Subscription(subscriber, channel);
    }

    public async Task<IReadOnlyList<SequencedTrackingEvent>> GetEventsSinceAsync(Guid orderId, long sinceSeq, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var raw = await db.ListRangeAsync(RecentKey(orderId));
        var events = new List<SequencedTrackingEvent>(raw.Length);
        foreach (var entry in raw)
        {
            if (!entry.HasValue) continue;
            var sequenced = JsonSerializer.Deserialize<SequencedTrackingEvent>((string)entry!, Json);
            if (sequenced is not null && sequenced.Seq > sinceSeq)
                events.Add(sequenced);
        }
        return events; // LIST preserves insertion order, so this is already oldest-first
    }

    private sealed class Subscription(ISubscriber subscriber, RedisChannel channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await subscriber.UnsubscribeAsync(channel);
    }
}
