using System.Text.RegularExpressions;

namespace Tadka.Gateway.Security;

/// <summary>
/// Origin lockdown (ADR-064). In the cloud the gateway's own *.azurecontainerapps.io URL is public, so
/// anyone could skip the Front Door WAF and CDN by calling it directly. Front Door stamps every request it
/// forwards with <c>X-Azure-FDID</c> = the profile's id; when <c>Gateway:RequiredFrontDoorId</c> is set,
/// any request without that exact value gets a 403.
///
/// OFF when the setting is empty or absent (local compose, dotnet run, tests): behavior is unchanged.
///
/// Exempt paths:
///   /health, /health/ready        platform and Front Door probes
///   /api/v1/orders/{id}/events    live tracking (SSE) goes to the gateway URL on purpose, not through the
///                                 CDN, because Front Door cuts long-lived responses (ADR-064). Each service
///                                 still validates the JWT (ADR-031), so exempt does not mean anonymous.
/// </summary>
public static partial class FrontDoorOriginLock
{
    public const string HeaderName = "X-Azure-FDID";
    public const string ConfigKey = "Gateway:RequiredFrontDoorId";

    [GeneratedRegex(@"^/api/v1/orders/[^/]+/events/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SsePath();

    public static bool IsExempt(PathString path)
    {
        var p = path.Value ?? string.Empty;
        return p.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/health/ready", StringComparison.OrdinalIgnoreCase)
            || SsePath().IsMatch(p);
    }

    public static bool HasMatchingId(IHeaderDictionary headers, string requiredId)
    {
        // Front Door sends one value. Several values (a client adding its own copy) never match.
        var values = headers[HeaderName];
        return values.Count == 1
            && string.Equals(values[0]?.Trim(), requiredId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the check when <see cref="ConfigKey"/> has a value; otherwise adds nothing.</summary>
    public static IApplicationBuilder UseFrontDoorOriginLock(this IApplicationBuilder app, IConfiguration config)
    {
        var requiredId = config[ConfigKey]?.Trim();
        if (string.IsNullOrEmpty(requiredId)) return app;

        return app.Use(async (ctx, next) =>
        {
            if (IsExempt(ctx.Request.Path) || HasMatchingId(ctx.Request.Headers, requiredId))
            {
                await next(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsync(
                """{"type":"about:blank","title":"Forbidden","status":403,"detail":"Call the public Front Door URL, not the origin."}""");
        });
    }
}
