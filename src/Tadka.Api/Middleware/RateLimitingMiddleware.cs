using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tadka.Api.Infrastructure.RateLimiting;

namespace Tadka.Api.Middleware;

/// <summary>
/// Day 6, Beat (ADR-049): per-IP, Redis-backed (shared across every replica). No JWT exists yet
/// (Day 10 adds auth) so IP is the only identity available - a real per-user limiter is a Day 10+
/// upgrade of this same lever, not a different mechanism. A 429 always carries Retry-After so a
/// well-behaved client backs off instead of hammering the limiter itself.
/// </summary>
public class RateLimitingMiddleware(RequestDelegate next, IRateLimiter limiter)
{
    private readonly RequestDelegate _next = next;
    private readonly IRateLimiter _limiter = limiter;

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await _limiter.CheckAsync(ip, context.RequestAborted);

        if (!result.Allowed)
        {
            var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(result.RetryAfterSeconds));
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = $"Rate limit exceeded. Retry after {retryAfterSeconds}s.",
                Type = "https://tools.ietf.org/html/rfc6585#section-4"
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return;
        }

        await _next(context);
    }
}
