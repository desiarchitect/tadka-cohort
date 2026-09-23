using System.Collections.Concurrent;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// In-memory stand-in for <see cref="ILocationStore"/> (real impl is Redis GEOADD/GEOPOS, ADR-034).
/// Reports <c>Enabled = true</c> — unlike <c>NullLocationStore</c> — so the location endpoint's
/// "Redis not configured" 503 guard doesn't block the wiring tests in <c>LocationTrackingTests</c> from
/// reaching the live-tracking publish call. Keeps the suite Redis-free (no real network dependency).
/// </summary>
public sealed class FakeLocationStore : ILocationStore
{
    public bool Enabled => true;

    private readonly ConcurrentDictionary<Guid, (double Latitude, double Longitude)> _positions = new();

    public Task SetAsync(Guid agentId, double latitude, double longitude)
    {
        _positions[agentId] = (latitude, longitude);
        return Task.CompletedTask;
    }

    public Task<(double Latitude, double Longitude)?> GetAsync(Guid agentId)
        => Task.FromResult(_positions.TryGetValue(agentId, out var pos) ? pos : ((double, double)?)null);
}
