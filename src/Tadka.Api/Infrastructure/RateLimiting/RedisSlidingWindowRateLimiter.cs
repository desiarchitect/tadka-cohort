using StackExchange.Redis;

namespace Tadka.Api.Infrastructure.RateLimiting;

/// <summary>
/// Sliding window via a Redis sorted set: every allowed request is a member scored by its own
/// timestamp; each check first evicts members older than the window, then counts what's left.
/// No boundary-burst problem (the window is always "now minus windowMs", not aligned to a clock
/// tick) - the fix for RedisFixedWindowRateLimiter's break. Costs more per check (a ZSET, not a
/// single counter) - the trade-off the break kit's comparison makes explicit.
/// </summary>
public sealed class RedisSlidingWindowRateLimiter(IConnectionMultiplexer redis, int limit, TimeSpan window) : IRateLimiter
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _limit = limit;
    private readonly TimeSpan _window = window;

    private const string Script = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local windowMs = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        local member = ARGV[4]
        redis.call('ZREMRANGEBYSCORE', key, 0, now - windowMs)
        local count = redis.call('ZCARD', key)
        if count < limit then
            redis.call('ZADD', key, now, member)
            redis.call('PEXPIRE', key, windowMs)
            return {1, -1}
        end
        local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
        local retryMs = windowMs
        if oldest[2] ~= nil then
            retryMs = windowMs - (now - tonumber(oldest[2]))
        end
        return {0, retryMs}
        """;

    public async Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        var redisKey = $"ratelimit:sliding:{key}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // A random suffix keeps concurrent requests in the same millisecond from colliding as
        // the same sorted-set member (which would under-count them as a single request).
        var member = $"{now}-{Guid.NewGuid():N}";

        var result = (RedisResult[])(await _db.ScriptEvaluateAsync(
            Script, [redisKey], [now, (long)_window.TotalMilliseconds, _limit, member]))!;

        var allowed = (long)result[0] == 1;
        var retryAfterMs = allowed ? 0 : (double)(long)result[1];
        return new RateLimitResult(allowed, retryAfterMs / 1000.0);
    }
}
