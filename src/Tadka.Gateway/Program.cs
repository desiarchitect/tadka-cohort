using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// YARP reverse proxy (ADR-035): one public entry point; routes load from config (ReverseProxy section).
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Edge rate-limiting (a cross-cutting concern that belongs at the gateway, not in each service). Fixed
// window per client IP. Tune `Gateway:RateLimitPerMinute` (low = easy to demo a 429).
var permitPerMinute = builder.Configuration.GetValue("Gateway:RateLimitPerMinute", 120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

app.UseRateLimiter();

// The gateway is a THIN edge: routing + rate-limit only. It forwards Authorization as-is; each service
// still validates the JWT itself (ADR-031, defense in depth — the gateway is not a trust boundary).
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "gateway" }));
app.MapReverseProxy();

app.Run();
