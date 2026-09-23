using System.Collections.Concurrent;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// Test double for <see cref="IOrderTrackingPublisher"/> — records every "publish" instead of talking to
/// Redis, so the wiring from <c>PUT /location</c> into the Day-6 live-tracking backplane can be asserted
/// deterministically, keeping the suite Redis-free (same convention as <c>DeliveryApiFactory</c>'s
/// NullLocationStore for Postgres/Redis-free tests). The publish/no-publish *decision* in the endpoint is
/// transport-agnostic, so exercising it against this fake proves the same wiring the real
/// <see cref="RedisOrderTrackingPublisher"/> would carry onto the SSE stream.
/// </summary>
public sealed class FakeOrderTrackingPublisher : IOrderTrackingPublisher
{
    public bool Enabled => true;

    private readonly ConcurrentBag<(Guid OrderId, double Latitude, double Longitude)> _published = [];
    public IReadOnlyCollection<(Guid OrderId, double Latitude, double Longitude)> Published => _published;

    public Task PublishLocationAsync(Guid orderId, double latitude, double longitude, CancellationToken ct = default)
    {
        _published.Add((orderId, latitude, longitude));
        return Task.CompletedTask;
    }
}
