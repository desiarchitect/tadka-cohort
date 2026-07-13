using StackExchange.Redis;
using Tadka.Api.Infrastructure.RateLimiting;
using Testcontainers.Redis;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Day 6, Beat (ADR-049): both rate-limiting algorithms against a REAL Redis (Testcontainers) -
/// exercised directly rather than through the full app, since <see cref="TadkaApiFactory"/>
/// deliberately runs Redis-free (ADR-018's test decision) to keep the main suite deterministic.
/// </summary>
public class RateLimiterTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
    private IConnectionMultiplexer _mux = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task FixedWindow_allows_up_to_the_limit_then_rejects()
    {
        var limiter = new RedisFixedWindowRateLimiter(_mux, limit: 5, window: TimeSpan.FromMinutes(1));
        var key = Guid.NewGuid().ToString();

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 7; i++)
            results.Add(await limiter.CheckAsync(key));

        Assert.Equal(5, results.Count(r => r.Allowed));
        Assert.Equal(2, results.Count(r => !r.Allowed));
        Assert.All(results.Where(r => !r.Allowed), r => Assert.True(r.RetryAfterSeconds > 0));
    }

    [Fact]
    public async Task SlidingWindow_allows_up_to_the_limit_then_rejects()
    {
        var limiter = new RedisSlidingWindowRateLimiter(_mux, limit: 5, window: TimeSpan.FromMinutes(1));
        var key = Guid.NewGuid().ToString();

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 7; i++)
            results.Add(await limiter.CheckAsync(key));

        Assert.Equal(5, results.Count(r => r.Allowed));
        Assert.Equal(2, results.Count(r => !r.Allowed));
    }

    [Fact]
    public async Task Different_keys_have_independent_limits()
    {
        var limiter = new RedisFixedWindowRateLimiter(_mux, limit: 2, window: TimeSpan.FromMinutes(1));
        var keyA = Guid.NewGuid().ToString();
        var keyB = Guid.NewGuid().ToString();

        // Exhaust A's limit.
        await limiter.CheckAsync(keyA);
        await limiter.CheckAsync(keyA);
        var aBlocked = await limiter.CheckAsync(keyA);

        // B is untouched — a different caller's identity has its own counter.
        var bAllowed = await limiter.CheckAsync(keyB);

        Assert.False(aBlocked.Allowed);
        Assert.True(bAllowed.Allowed);
    }

    [Fact]
    public async Task SlidingWindow_ZSET_never_exceeds_the_limit_under_concurrent_requests()
    {
        // The concurrency-safety property the Lua script exists for: N callers racing the SAME
        // key must never collectively get more than `limit` admissions, even though the
        // check-then-act would race without the atomic script.
        var limiter = new RedisSlidingWindowRateLimiter(_mux, limit: 10, window: TimeSpan.FromMinutes(1));
        var key = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => limiter.CheckAsync(key)));

        Assert.Equal(10, results.Count(r => r.Allowed));
    }
}
