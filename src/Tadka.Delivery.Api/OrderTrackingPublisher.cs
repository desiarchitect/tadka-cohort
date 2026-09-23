using System.Text.Json;
using StackExchange.Redis;

namespace Tadka.Delivery.Api;

/// <summary>
/// Publishes a rider-location ping onto Day 6's live-tracking backplane (ADR-020, ADR-036): the same
/// Redis pub/sub channel <c>order:{orderId}</c> that <c>Tadka.Api</c>'s <c>RedisOrderTrackingBus</c>
/// already publishes order-status events to and that the customer's <c>GET /orders/{id}/events</c> SSE
/// stream already subscribes to. Delivery.Api does NOT reference the monolith project (ADR-033 — separate
/// deployable) so this publishes a wire-compatible JSON shape directly, the same way <c>Messaging.cs</c>
/// duplicates the Kafka message contracts on each side of a service boundary.
/// </summary>
public interface IOrderTrackingPublisher
{
    bool Enabled { get; }
    Task PublishLocationAsync(Guid orderId, double latitude, double longitude, CancellationToken ct = default);
}

public sealed class RedisOrderTrackingPublisher(IConnectionMultiplexer redis) : IOrderTrackingPublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => true;

    public Task PublishLocationAsync(Guid orderId, double latitude, double longitude, CancellationToken ct = default)
    {
        var trackingEvent = new OrderTrackingEvent(orderId, "RiderLocation", "Rider location updated.", DateTime.UtcNow, latitude, longitude);
        var channel = RedisChannel.Literal($"order:{orderId}");
        return redis.GetSubscriber().PublishAsync(channel, JsonSerializer.Serialize(trackingEvent, Json));
    }
}

/// <summary>No Redis configured ⇒ no-op, same convention as <see cref="NullLocationStore"/> (tests stay Redis-free).</summary>
public sealed class NullOrderTrackingPublisher : IOrderTrackingPublisher
{
    public bool Enabled => false;
    public Task PublishLocationAsync(Guid orderId, double latitude, double longitude, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Wire-compatible mirror of <c>Tadka.Api.Infrastructure.Realtime.OrderTrackingEvent</c> (ADR-020/036).
/// Kept in sync by hand across the service boundary — same convention as <c>OrderConfirmedMessage</c> /
/// <c>DeliveryAssignedMessage</c> in <c>Messaging/Messaging.cs</c>. Property names must match exactly
/// (System.Text.Json's Web defaults camel-case them) since the monolith deserializes into its own type.
/// </summary>
public sealed record OrderTrackingEvent(
    Guid OrderId,
    string Status,
    string Message,
    DateTime Timestamp,
    double? Latitude = null,
    double? Longitude = null);
