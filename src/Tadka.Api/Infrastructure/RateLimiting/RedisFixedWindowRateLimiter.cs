using StackExchange.Redis;

namespace Tadka.Api.Infrastructure.RateLimiting;

/// <summary>
/// Fixed window: INCR a counter keyed by (identity, current window), EXPIRE on first hit.
/// Cheap (one Redis round trip, O(1) memory) but lets a caller burst up to 2x the limit across
/// a window boundary (e.g. 60 requests at 59.9s + 60 more at 60.1s = 120 requests in 200ms) -
/// that boundary burst is exactly what the break kit demonstrates against SlidingWindow.
/// </summary>
public sealed class RedisFixedWindowRateLimiter(IConnectionMultiplexer redis, int limit, TimeSpan window) : IRateLimiter
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly int _limit = limit;
    private readonly TimeSpan _window = window;

    private const string Script = """
        local key = KEYS[1]
        local limit = tonumber(ARGV[1])
        local windowSeconds = tonumber(ARGV[2])
        local count = redis.call('INCR', key)
        if count == 1 then
            redis.call('EXPIRE', key, windowSeconds)
        end
        if count > limit then
            return {0, redis.call('TTL', key)}
        end
        return {1, -1}
        """;

    public async Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        var windowBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)_window.TotalSeconds;
        var redisKey = $"ratelimit:fixed:{key}:{windowBucket}";

        var result = (RedisResult[])(await _db.ScriptEvaluateAsync(
            Script, [redisKey], [_limit, (int)_window.TotalSeconds]))!;

        var allowed = (long)result[0] == 1;
        var retryAfter = allowed ? 0 : (double)(long)result[1];
        return new RateLimitResult(allowed, retryAfter);
    }
}
