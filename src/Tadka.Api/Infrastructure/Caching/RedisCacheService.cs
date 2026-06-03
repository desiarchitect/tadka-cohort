using System.Text.Json;
using StackExchange.Redis;

namespace Tadka.Api.Infrastructure.Caching;

/// <summary>
/// Cache-aside over Redis (ADR-018) with single-flight stampede protection (ADR-019).
/// On a miss, exactly one caller acquires a short SET NX EX lock and refreshes from the DB while
/// others briefly wait and re-read. Any Redis failure degrades gracefully to the DB factory —
/// Redis is a performance dependency, not a correctness one.
/// </summary>
public sealed class RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger) : ICacheService
{
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly ILogger<RedisCacheService> _logger = logger;
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();

            // 1) Cache hit — the DB never sees this request.
            var hit = await db.StringGetAsync(key);
            if (hit.HasValue)
                return JsonSerializer.Deserialize<T>((string)hit!, Json);

            // 2) Miss → single-flight: try to become the one refresher (ADR-019).
            var lockKey = $"lock:{key}";
            var token = Guid.NewGuid().ToString("N");
            var isRefresher = await db.StringSetAsync(lockKey, token, LockTtl, When.NotExists);

            if (isRefresher)
            {
                try
                {
                    var value = await factory();
                    if (value is not null)
                        await db.StringSetAsync(key, JsonSerializer.Serialize(value, Json), ttl);
                    return value;
                }
                finally
                {
                    // Release the lock only if we still own it (don't delete someone else's).
                    if ((string?)await db.StringGetAsync(lockKey) == token)
                        await db.KeyDeleteAsync(lockKey);
                }
            }

            // 3) Someone else is refreshing — wait briefly and re-read, then give up to the DB.
            for (var attempt = 0; attempt < 5 && !ct.IsCancellationRequested; attempt++)
            {
                await Task.Delay(80, ct);
                hit = await db.StringGetAsync(key);
                if (hit.HasValue)
                    return JsonSerializer.Deserialize<T>((string)hit!, Json);
            }
            return await factory(); // correctness over purity
        }
        catch (RedisException ex)
        {
            // Redis blip → serve from the DB rather than fail the request.
            _logger.LogWarning(ex, "Redis unavailable for key {Key}; falling back to the database.", key);
            return await factory();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }
        catch (RedisException ex)
        {
            // A missed delete is bounded by the TTL safety net (ADR-018) — log and move on.
            _logger.LogWarning(ex, "Redis unavailable invalidating key {Key}; TTL will bound staleness.", key);
        }
    }
}
