namespace Tadka.Api.Infrastructure.RateLimiting;

/// <summary>No Redis configured -> no rate limiting. Matches the optional-infra convention used
/// throughout (NullCacheService, NullOrderTrackingBus): dev/tests run unchanged without Redis.</summary>
public sealed class NullRateLimiter : IRateLimiter
{
    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(new RateLimitResult(true, 0));
}
