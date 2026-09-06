using FluentValidation;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Tadka.Api.Data;
using Tadka.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Day 6, Beat (ADR-048): brotli (preferred) + gzip on JSON responses. Payload-size win on any
// list/menu response; costs a little CPU per request — cheap at Tadka's scale, revisit if a
// profiler ever says otherwise.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

builder.Services.AddDbContext<TadkaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TadkaDb")));

// Read-replica context (ADR-016): NoTracking, pointed at the replica. Falls back to the primary
// connection when no replica is configured, so single-Postgres dev and the test suite still work.
builder.Services.AddDbContext<TadkaReadDbContext>(options =>
    options.UseNpgsql(
            builder.Configuration.GetConnectionString("TadkaDbReplica")
            ?? builder.Configuration.GetConnectionString("TadkaDb"))
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

// Repositories & Factories
builder.Services.AddSingleton<Tadka.Api.Infrastructure.Security.UrlSigner>();
builder.Services.AddScoped<Tadka.Api.Data.Repositories.IOrderRepository, Tadka.Api.Data.Repositories.OrderRepository>();
builder.Services.AddScoped<Tadka.Api.Data.Repositories.IIdempotencyStore, Tadka.Api.Data.Repositories.IdempotencyStore>();
builder.Services.AddScoped<Tadka.Api.Domain.Orders.OrderFactory>();

// In-process events via MediatR (ADR-022, supersedes the Day-4 hand-rolled dispatcher). One call
// auto-registers every INotificationHandler<T> in the assembly — order notification + SSE backplane
// (ADR-020) + the Payment module's OrderPlaced handler + the order's reaction to payment settling.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

// Redis (ADR-018/019/020): cache-aside + single-flight lock + live-tracking pub/sub.
// Optional — if no "Redis" connection string is configured, the cache is a no-op and live
// tracking returns 503, so single-Postgres dev and the test suite run unchanged.
//
// Day 6 scale-out beat: "Cache:Mode=InMemory" swaps the cache for a process-local fallback
// (still connects to Redis for live tracking — only the cache layer changes) to demonstrate why
// falling back to an in-process cache under multi-instance load is a trap: each replica then
// disagrees with the others about cached values (menu prices). Default is unset (Redis if
// configured, else the no-op) — the shipped behavior is unchanged.
var redisConnection = builder.Configuration.GetConnectionString("Redis");
var cacheMode = builder.Configuration.GetValue<string>("Cache:Mode");

builder.Services.AddMemoryCache();

if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Realtime.IOrderTrackingBus, Tadka.Api.Infrastructure.Realtime.RedisOrderTrackingBus>();
}
else
{
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Realtime.IOrderTrackingBus, Tadka.Api.Infrastructure.Realtime.NullOrderTrackingBus>();
}

if (string.Equals(cacheMode, "InMemory", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.InMemoryFallbackCacheService>();
else if (!string.IsNullOrWhiteSpace(redisConnection))
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.RedisCacheService>();
else
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.NullCacheService>();

var rateLimitPerMinute = builder.Configuration.GetValue("RateLimit:PerMinute", 120);
var rateLimitAlgorithm = builder.Configuration.GetValue<string>("RateLimit:Algorithm") ?? "FixedWindow";
var rateLimitWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimit:WindowSeconds", 60));
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.RateLimiting.IRateLimiter>(sp =>
    {
        var mux = sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        return string.Equals(rateLimitAlgorithm, "SlidingWindow", StringComparison.OrdinalIgnoreCase)
            ? new Tadka.Api.Infrastructure.RateLimiting.RedisSlidingWindowRateLimiter(mux, rateLimitPerMinute, rateLimitWindow)
            : new Tadka.Api.Infrastructure.RateLimiting.RedisFixedWindowRateLimiter(mux, rateLimitPerMinute, rateLimitWindow);
    });
}
else
{
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.RateLimiting.IRateLimiter, Tadka.Api.Infrastructure.RateLimiting.NullRateLimiter>();
}

// ── Payment, now a SEPARATE service (ADR-024/025/026) ───────────────────────────────────────────
builder.Services.Configure<Tadka.Api.Modules.Payments.PaymentClientOptions>(
    builder.Configuration.GetSection(Tadka.Api.Modules.Payments.PaymentClientOptions.SectionName));

builder.Services.AddSingleton<Tadka.Api.Modules.Payments.PaymentClientResilience>();
builder.Services.AddSingleton<Tadka.Api.Modules.Payments.PaymentWorkChannel>();
builder.Services.AddHostedService<Tadka.Api.Modules.Payments.PaymentProcessor>();

builder.Services.AddHttpClient<Tadka.Api.Modules.Payments.IPaymentClient, Tadka.Api.Modules.Payments.HttpPaymentClient>((sp, client) =>
{
    var url = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Tadka.Api.Modules.Payments.PaymentClientOptions>>().Value.ServiceUrl;
    if (!string.IsNullOrWhiteSpace(url))
        client.BaseAddress = new Uri(url);
});

var app = builder.Build();

// Automatically apply migrations on startup (great for cohort local dev). The monolith now owns ONLY the
// core schema — the Payment service migrates its OWN database in its OWN process (ADR-026). A broken
// payment DB can no longer stop the monolith from booting.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<TadkaDbContext>().Database.Migrate();
}


if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Tadka API";
        options.Theme = ScalarTheme.DeepSpace;
    });
}

app.UseResponseCompression();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseHttpsRedirection();

// Day 6 scale-out beat: stamps which replica answered so the Demo Console (and curl -i) can show
// a round-robin/state-divergence demo directly. INSTANCE_NAME is set per-container in the
// scale-out compose profile; a single local `dotnet run` leaves it unset (header omitted).
var instanceName = builder.Configuration["INSTANCE_NAME"];
if (!string.IsNullOrWhiteSpace(instanceName))
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Tadka-Instance"] = instanceName;
        await next();
    });
}

app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the integration test project can boot the real app via WebApplicationFactory<Program>.
public partial class Program { }
