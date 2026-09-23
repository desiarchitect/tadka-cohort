using FluentValidation;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Tadka.Api.Data;
using Tadka.Api.Middleware;
using Tadka.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Observability (ADR-040): structured JSON logs + OTEL traces/metrics over OTLP. OTLP export is gated on
// OTEL_EXPORTER_OTLP_ENDPOINT, so the test suite + single-process dev are unchanged (no stack required).
builder.AddTadkaTelemetry("Tadka.Api");

// Field-level PII encryption (ADR-052) — configured before ANY DbContext model is built (migrations
// trigger model build), since UserConfiguration reads FieldCipher while building the model.
// Key lives in appsettings.Development.json only (not hardcoded here). Non-Development requires an
// explicit Demo:EncryptionKey (or EncryptPiiAtRest=false) — secrets manager / KMS in real deploys.
{
    var encryptPii = builder.Configuration.GetValue("Demo:EncryptPiiAtRest", false);
    var encryptionKey = builder.Configuration["Demo:EncryptionKey"];
    if (encryptPii && string.IsNullOrWhiteSpace(encryptionKey))
    {
        if (builder.Environment.IsDevelopment())
        {
            // Local misconfig: prefer clear failure over a silent hardcoded key in source.
            throw new InvalidOperationException(
                "Demo:EncryptPiiAtRest is true but Demo:EncryptionKey is missing. " +
                "Set it in appsettings.Development.json (dev-only key) or set EncryptPiiAtRest=false.");
        }
        throw new InvalidOperationException(
            "Demo:EncryptionKey is required when Demo:EncryptPiiAtRest is true outside Development. " +
            "Supply from a secrets manager / KMS — never commit production keys.");
    }
    Tadka.Api.Infrastructure.Security.FieldCipher.Configure(encryptPii, encryptionKey);
}

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

// Transient-fault retry (ADR-064, Day-14 cloud failover). OFF by default, so local dev and the tests
// behave exactly as before. ON in the cloud `ha` session: a managed Postgres failover drops every open
// connection for tens of seconds, and EF's retrying strategy replays the failed unit instead of throwing a
// 500. Explicit transactions must then run inside CreateExecutionStrategy().ExecuteAsync (see OutboxRelay).
var dbRetry = builder.Configuration.GetValue("Database:EnableRetryOnFailure", false);
var dbMaxRetries = builder.Configuration.GetValue("Database:MaxRetryCount", 6);
var dbMaxRetryDelay = TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MaxRetryDelaySeconds", 30));

builder.Services.AddDbContext<TadkaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TadkaDb"), npgsql =>
    {
        if (dbRetry) npgsql.EnableRetryOnFailure(dbMaxRetries, dbMaxRetryDelay, null);
    }));

// Read-replica context (ADR-016): NoTracking, pointed at the replica. Falls back to the primary
// connection when no replica is configured, so single-Postgres dev and the test suite still work.
builder.Services.AddDbContext<TadkaReadDbContext>(options =>
    options.UseNpgsql(
            builder.Configuration.GetConnectionString("TadkaDbReplica")
            ?? builder.Configuration.GetConnectionString("TadkaDb"), npgsql =>
            {
                if (dbRetry) npgsql.EnableRetryOnFailure(dbMaxRetries, dbMaxRetryDelay, null);
            })
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

// Repositories & Factories
builder.Services.AddSingleton<Tadka.Api.Infrastructure.Security.UrlSigner>();
builder.Services.AddScoped<Tadka.Api.Data.Repositories.IOrderRepository, Tadka.Api.Data.Repositories.OrderRepository>();
builder.Services.AddScoped<Tadka.Api.Data.Repositories.IIdempotencyStore, Tadka.Api.Data.Repositories.IdempotencyStore>();
builder.Services.AddScoped<Tadka.Api.Domain.Orders.OrderFactory>();

// Server-side pricing source (ADR-037). Restaurant was extracted (ADR-036), so order pricing no longer
// reads an in-process Restaurant aggregate. Default = the local read model (available even when Restaurant
// is down). `Ordering:RestaurantReadMode = SyncHttp` swaps in a synchronous HTTP read to demonstrate the
// temporal coupling the read model avoids (the Day-12 "Restaurant down â†’ orders still flow" demo).
var readMode = builder.Configuration["Ordering:RestaurantReadMode"] ?? "LocalReplica";
if (string.Equals(readMode, "SyncHttp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<Tadka.Api.Domain.Orders.IRestaurantPricingSource,
        Tadka.Api.Infrastructure.RestaurantReadModel.HttpRestaurantPricingSource>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:Restaurant:BaseUrl"] ?? "http://localhost:5260");
        c.Timeout = TimeSpan.FromSeconds(2); // fail fast â€” but a down peer still fails the order (the point)
    });
}
else
{
    builder.Services.AddScoped<Tadka.Api.Domain.Orders.IRestaurantPricingSource,
        Tadka.Api.Infrastructure.RestaurantReadModel.LocalReplicaPricingSource>();
}

// In-process events via MediatR (ADR-022, supersedes the Day-4 hand-rolled dispatcher). One call
// auto-registers every INotificationHandler<T> in the assembly â€” order notification + SSE backplane
// (ADR-020) + the Payment module's OrderPlaced handler + the order's reaction to payment settling.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

// Day 11 refund saga levers (ADR-045/046): restaurant accept/reject + choreography vs orchestration naming.
builder.Services.Configure<Tadka.Api.Domain.Restaurants.RestaurantAcceptanceOptions>(
    builder.Configuration.GetSection(Tadka.Api.Domain.Restaurants.RestaurantAcceptanceOptions.SectionName));
builder.Services.Configure<Tadka.Api.Infrastructure.Messaging.SagaOptions>(
    builder.Configuration.GetSection(Tadka.Api.Infrastructure.Messaging.SagaOptions.SectionName));
builder.Services.AddScoped<Tadka.Api.Infrastructure.Messaging.RefundSagaOrchestrator>();
builder.Services.AddScoped<Tadka.Api.Infrastructure.Messaging.RestaurantResponseHandler>();

// Redis (ADR-018/019/020): cache-aside + single-flight lock + live-tracking pub/sub.
// Optional â€” if no "Redis" connection string is configured, the cache is a no-op and live
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
    // AbortOnConnectFail=false (explicit, ADR-064): if Redis is down or failing over at boot, the
    // multiplexer keeps reconnecting in the background instead of throwing. The cache already falls
    // through to the DB on RedisException (ADR-044), so a Redis blip costs latency, not errors.
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    {
        var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
        redisOptions.AbortOnConnectFail = false;
        return StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions);
    });
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Realtime.IOrderTrackingBus, Tadka.Api.Infrastructure.Realtime.RedisOrderTrackingBus>();
}
else
{
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Realtime.IOrderTrackingBus, Tadka.Api.Infrastructure.Realtime.NullOrderTrackingBus>();
}

// â”€â”€ Payment over KAFKA, durable (ADR-027/028/029) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Day 9: the Day-8 synchronous HTTP bridge is replaced by an async event backbone. Order creation writes
// an `order-placed` row to the transactional Outbox (in the order's transaction); the OutboxRelay
// publishes it to Kafka; the Payment service consumes it, charges, and publishes `payment-results`; the
// PaymentResultsConsumer below converges the order (Saga). A down Payment service now means messages
// WAIT, not lost charges. Kafka is OFF when no BootstrapServers are configured (tests / single-process dev):
// the Outbox row is still written (harmless), but the relay + consumer don't start.
builder.Services.Configure<Tadka.Api.Infrastructure.Messaging.KafkaOptions>(
    builder.Configuration.GetSection(Tadka.Api.Infrastructure.Messaging.KafkaOptions.SectionName));

var kafkaOptions = builder.Configuration
    .GetSection(Tadka.Api.Infrastructure.Messaging.KafkaOptions.SectionName)
    .Get<Tadka.Api.Infrastructure.Messaging.KafkaOptions>();
if (kafkaOptions?.Enabled == true)
{
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Messaging.KafkaProducer>();
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.OutboxRelay>();
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.PaymentResultsConsumer>();
    // Keep the local price replica fresh from the Restaurant service's menu-updated events (ADR-037).
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.MenuUpdatedConsumer>();
    // ADR-045: surface payment-refunded on the live-tracking bus after compensation settles.
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.PaymentRefundedConsumer>();
    // ADR-062: Restaurant.Api multi-service accept/reject path (DecisionMode=Service).
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.RestaurantResponseConsumer>();
}

// â”€â”€ Authentication & Authorization (ADR-030/031) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Stateless JWT bearer; each service verifies the token itself (defense in depth â€” the Payment service
// validates the SAME key). Authorization is RBAC (the `role` claim) + resource-ownership checks done in
// the controllers (the `sub` / `restaurantId` claims).
builder.Services.Configure<Tadka.Api.Auth.JwtOptions>(builder.Configuration.GetSection(Tadka.Api.Auth.JwtOptions.SectionName));
builder.Services.Configure<Tadka.Api.Middleware.LoadSheddingOptions>(
    builder.Configuration.GetSection(Tadka.Api.Middleware.LoadSheddingOptions.SectionName));
builder.Services.Configure<Tadka.Api.Middleware.BackpressureOptions>(
    builder.Configuration.GetSection(Tadka.Api.Middleware.BackpressureOptions.SectionName));
builder.Services.AddSingleton<Tadka.Api.Auth.TokenService>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<Tadka.Api.Domain.Users.User>,
    Microsoft.AspNetCore.Identity.PasswordHasher<Tadka.Api.Domain.Users.User>>();

var jwt = builder.Configuration.GetSection(Tadka.Api.Auth.JwtOptions.SectionName).Get<Tadka.Api.Auth.JwtOptions>() ?? new();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep our own claim names ("role"/"sub"); don't remap to the long WS-* URIs, or
        // [Authorize(Roles = â€¦)] would never see the role claim from our JsonWebToken (ADR-030/031).
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();

if (string.Equals(cacheMode, "InMemory", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.InMemoryFallbackCacheService>();
else if (!string.IsNullOrWhiteSpace(redisConnection))
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.RedisCacheService>();
else
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.NullCacheService>();

// Day 6, Beat (ADR-049): distributed rate limiting. "RateLimit:Algorithm=FixedWindow" (default)
// | "SlidingWindow" — same Redis connection, same limit/window, different algorithm, so the
// break kit can compare them head to head. "RateLimit:WindowSeconds" (default 60) is
// configurable so the break kit can demo the fixed-window boundary burst on a short (e.g. 3s)
// window instead of waiting for a real minute boundary each time. No Redis configured -> no
// limiting (optional infra, matches the cache/tracking pattern above).
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

// Feature flags (ADR-058): percentage rollout with stable hashing; Redis override Flags:{name}.
builder.Services.AddSingleton<Tadka.Api.Infrastructure.FeatureFlags.IFeatureFlagService>(sp =>
    new Tadka.Api.Infrastructure.FeatureFlags.FeatureFlagService(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
        sp.GetRequiredService<ILogger<Tadka.Api.Infrastructure.FeatureFlags.FeatureFlagService>>()));

var app = builder.Build();

// Apply migrations on startup, then idempotently seed known demo users with real password hashes + roles
// (so login works on a fresh DB). The monolith owns ONLY the core schema; the Payment service migrates its
// own database in its own process (ADR-026).
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    sp.GetRequiredService<TadkaDbContext>().Database.Migrate();
    await Tadka.Api.Auth.AuthSeeder.SeedAsync(
        sp.GetRequiredService<TadkaDbContext>(),
        sp.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<Tadka.Api.Domain.Users.User>>());
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
// Order: shed first (cheap 503), then admit (concurrency), then rate-limit (per-IP).
app.UseMiddleware<Tadka.Api.Middleware.LoadSheddingMiddleware>();
app.UseMiddleware<Tadka.Api.Middleware.BackpressureMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseHttpsRedirection();
app.UseAuthentication();

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
