using Microsoft.AspNetCore.Mvc;

namespace Tadka.Api.Middleware;

/// <summary>
/// Priority load shedding (ADR-060). Under extreme load, sheddable paths return 503 quickly so
/// critical paths (place order, pay, auth, health) keep capacity.
/// Levers: <c>LoadShed:Enabled</c>, optional auto when concurrent &gt; threshold (via <c>LoadShed:Force</c>).
/// </summary>
public sealed class LoadSheddingMiddleware(RequestDelegate next, IConfiguration config)
{
    private static readonly HashSet<string> CriticalPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/api/v1/auth",
        "/api/v1/orders",
        "/api/v1/payments",
        "/api/v1/deliveries"
    };

    // Explicit sheddable (everything else under /api is treated as sheddable when enabled).
    private static readonly string[] SheddableContains =
    [
        "/invoice",
        "/history",
        "/demo"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var enabled = config.GetValue("LoadShed:Enabled", false)
                      || config.GetValue("LoadShed:Force", false);
        if (!enabled)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        if (IsCritical(path))
        {
            await next(context);
            return;
        }

        // Sheddable: non-critical API under load
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || SheddableContains.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Degraded",
                Detail = "Load shedding active: this non-critical path is temporarily unavailable. Ordering and payment still work.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.4"
            });
            return;
        }

        await next(context);
    }

    private static bool IsCritical(string path)
    {
        foreach (var p in CriticalPrefixes)
        {
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
