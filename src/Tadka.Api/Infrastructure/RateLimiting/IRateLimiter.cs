namespace Tadka.Api.Infrastructure.RateLimiting;

/// <summary>
/// Day 6, Beat (ADR-049): distributed rate limiting. Redis-backed so the limit is shared across
/// every replica (as opposed to a per-instance in-memory counter, which under the Day 6
/// scale-out profile would silently give callers 3x the intended limit — one counter per
/// replica). No JWT exists yet (Day 10), so the limit key is the caller's IP.
/// </summary>
public interface IRateLimiter
{
    Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default);
}

public readonly record struct RateLimitResult(bool Allowed, double RetryAfterSeconds);
