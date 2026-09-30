using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Tadka.Api.Infrastructure.RateLimiting;

namespace Tadka.Api.Auth;

/// <summary>
/// The tight limit on the credential endpoints (<c>/login</c>, <c>/register</c>, <c>/refresh</c>): 5 requests per
/// 10 seconds per IP by default (<c>Auth:RateLimit</c>, ADR-065). It is deliberately separate from the general
/// per-IP limiter (ADR-049, 120 a minute): a legitimate client can burst through the API, but no human retries
/// a login 50 times a minute, so the two endpoints families need very different numbers.
///
/// It reuses this branch's limiter abstraction (<see cref="IRateLimiter"/>): Redis-backed when Redis is
/// configured, so the limit is shared by every replica behind the load balancer (an in-memory counter would give
/// each of N replicas its own budget), and an in-process counter otherwise (tests, single-instance dev).
/// Keys are prefixed <c>auth:</c> so they never collide with the general limiter's per-IP keys.
/// </summary>
public interface IAuthThrottle
{
    Task<RateLimitResult> CheckAsync(string ip, CancellationToken ct = default);
}

public sealed class AuthThrottle(IRateLimiter inner) : IAuthThrottle
{
    public Task<RateLimitResult> CheckAsync(string ip, CancellationToken ct = default)
        => inner.CheckAsync("auth:" + ip, ct);
}

/// <summary>Fixed-window counter per key, in this process only. The fallback when there is no Redis.</summary>
public sealed class InProcessFixedWindowRateLimiter(int limit, TimeSpan window) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, (long Bucket, int Count)> _counters = new();

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        var windowSeconds = (long)window.TotalSeconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = now / windowSeconds;

        var entry = _counters.AddOrUpdate(key,
            _ => (bucket, 1),
            (_, existing) => existing.Bucket == bucket ? (bucket, existing.Count + 1) : (bucket, 1));

        if (entry.Count <= limit)
            return Task.FromResult(new RateLimitResult(true, 0));

        var secondsLeft = (bucket + 1) * windowSeconds - now;
        return Task.FromResult(new RateLimitResult(false, Math.Max(1, secondsLeft)));
    }
}

/// <summary>Rejects a credential-endpoint request with 429 + <c>Retry-After</c> once the IP is over its budget.</summary>
public sealed class AuthThrottleFilter(IAuthThrottle throttle) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await throttle.CheckAsync(ip, context.HttpContext.RequestAborted);
        if (result.Allowed)
        {
            await next();
            return;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(result.RetryAfterSeconds));
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Detail = $"Too many attempts. Retry after {retryAfter}s.",
            Type = "https://tools.ietf.org/html/rfc6585#section-4"
        })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentTypes = { "application/problem+json" }
        };
    }
}
