using StackExchange.Redis;

namespace Tadka.Delivery.Api;

/// <summary>
/// Live rider location in Redis-geo (ADR-034): <c>GEOADD</c> overwrites the latest position (no growing
/// table), <c>GEOPOS</c> reads it sub-ms. Optional — with no Redis configured it's a no-op (tests stay
/// Redis-free; the live demo uses real Redis).
/// </summary>
public interface ILocationStore
{
    bool Enabled { get; }
    Task SetAsync(Guid agentId, double latitude, double longitude);
    Task<(double Latitude, double Longitude)?> GetAsync(Guid agentId);
}

public sealed class RedisLocationStore(IConnectionMultiplexer redis) : ILocationStore
{
    private const string Key = "delivery:agents";
    private readonly IDatabase _db = redis.GetDatabase();

    public bool Enabled => true;

    public Task SetAsync(Guid agentId, double latitude, double longitude)
        => _db.GeoAddAsync(Key, longitude, latitude, agentId.ToString());

    public async Task<(double, double)?> GetAsync(Guid agentId)
    {
        var pos = await _db.GeoPositionAsync(Key, agentId.ToString());
        return pos is { } p ? (p.Latitude, p.Longitude) : null;
    }
}

public sealed class NullLocationStore : ILocationStore
{
    public bool Enabled => false;
    public Task SetAsync(Guid agentId, double latitude, double longitude) => Task.CompletedTask;
    public Task<(double, double)?> GetAsync(Guid agentId) => Task.FromResult<(double, double)?>(null);
}
