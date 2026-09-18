using System.Text.Json;
using StackExchange.Redis;

namespace Tadka.Restaurant.Api.Caching;

/// <summary>
/// Cache-aside facade (ADR-018) — moved WITH the Restaurant service (ADR-036): the read-heavy/cached
/// service owns its own cache. <see cref="GetOrSetAsync"/> returns the cached value, or runs the factory
/// (DB) on a miss + populates, protected by a single-flight lock (ADR-019). Redis is a performance
/// dependency, not a correctness one — absent/down ⇒ fall back to the DB.
/// </summary>
public interface ICacheService
{
    Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

/// <summary>Cache-aside over Redis (ADR-018) + single-flight stampede protection (ADR-019).</summary>
public sealed class RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger) : ICacheService
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Compare-and-delete in one round trip. GET-then-DEL has a gap: the lock can expire
    // and be re-acquired between GET and DEL, and an unconditional DEL would drop the new owner's lock.
    private const string ReleaseIfOwnerScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            var db = redis.GetDatabase();

            var hit = await db.StringGetAsync(key);
            if (hit.HasValue) return JsonSerializer.Deserialize<T>((string)hit!, Json);

            var lockKey = $"lock:{key}";
            var token = Guid.NewGuid().ToString("N");
            var isRefresher = await db.StringSetAsync(lockKey, token, LockTtl, When.NotExists);

            if (isRefresher)
            {
                try
                {
                    var value = await factory();
                    if (value is not null) await db.StringSetAsync(key, JsonSerializer.Serialize(value, Json), ttl);
                    return value;
                }
                finally
                {
                    await db.ScriptEvaluateAsync(ReleaseIfOwnerScript, [lockKey], [token]);
                }
            }

            for (var attempt = 0; attempt < 5 && !ct.IsCancellationRequested; attempt++)
            {
                await Task.Delay(80, ct);
                hit = await db.StringGetAsync(key);
                if (hit.HasValue) return JsonSerializer.Deserialize<T>((string)hit!, Json);
            }
            return await factory();
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis unavailable for key {Key}; falling back to the database.", key);
            return await factory();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try { await redis.GetDatabase().KeyDeleteAsync(key); }
        catch (RedisException ex) { logger.LogWarning(ex, "Redis unavailable invalidating {Key}; TTL bounds staleness.", key); }
    }
}

/// <summary>No-op cache when Redis is not configured (tests / single-process dev).</summary>
public sealed class NullCacheService : ICacheService
{
    public Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default) => factory();
    public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
}
