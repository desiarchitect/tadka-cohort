using System.Text.Json;
using StackExchange.Redis;

namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// Redis pub/sub implementation of the live-tracking backplane (ADR-020). Publishing and subscribing
/// both go through Redis channel <c>order:{id}</c>, so the publishing instance and the
/// connection-holding instance need not be the same box — the backplane bridges them.
/// </summary>
public sealed class RedisOrderTrackingBus(IConnectionMultiplexer redis) : IOrderTrackingBus
{
    private readonly IConnectionMultiplexer _redis = redis;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => true;

    private static RedisChannel Channel(Guid orderId) => RedisChannel.Literal($"order:{orderId}");

    public async Task PublishAsync(OrderTrackingEvent trackingEvent, CancellationToken ct = default)
        => await _redis.GetSubscriber()
            .PublishAsync(Channel(trackingEvent.OrderId), JsonSerializer.Serialize(trackingEvent, Json));

    public async Task<IAsyncDisposable> SubscribeAsync(Guid orderId, Func<OrderTrackingEvent, Task> onEvent, CancellationToken ct = default)
    {
        var subscriber = _redis.GetSubscriber();
        var channel = Channel(orderId);

        await subscriber.SubscribeAsync(channel, async (_, message) =>
        {
            if (!message.HasValue) return;
            var trackingEvent = JsonSerializer.Deserialize<OrderTrackingEvent>((string)message!, Json);
            if (trackingEvent is not null) await onEvent(trackingEvent);
        });

        return new Subscription(subscriber, channel);
    }

    private sealed class Subscription(ISubscriber subscriber, RedisChannel channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await subscriber.UnsubscribeAsync(channel);
    }
}
