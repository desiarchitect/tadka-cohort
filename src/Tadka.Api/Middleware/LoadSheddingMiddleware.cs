using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Tadka.Api.Middleware;

/// <summary>Levers: <c>LoadShed:Enabled</c>, optional auto when concurrent &gt; threshold (via <c>LoadShed:Force</c>).</summary>
public sealed class LoadSheddingOptions
{
    public const string SectionName = "LoadShed";
    public bool Enabled { get; set; }
    public bool Force { get; set; }
}

/// <summary>
/// Priority load shedding (ADR-060). Under extreme load, sheddable paths return 503 quickly so
/// critical paths (place order, pay, auth, health) keep capacity.
/// </summary>
public sealed class LoadSheddingMiddleware(RequestDelegate next, IOptionsMonitor<LoadSheddingOptions> options)
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
        var enabled = options.CurrentValue.Enabled || options.CurrentValue.Force;
        if (!enabled)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        // Explicitly sheddable paths win over the critical prefixes: order history and invoices live under
        // /api/v1/orders (a critical prefix), but they are the first things to drop (ADR-060). Place-order,
        // order status and payments stay admitted.
        var explicitlySheddable = SheddableContains.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (!explicitlySheddable && IsCritical(path))
        {
            await next(context);
            return;
        }

        // Sheddable: non-critical API under load
        if (explicitlySheddable || path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
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
