using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Tadka.Restaurant.Api;
using Tadka.Restaurant.Api.Auth;
using Tadka.Restaurant.Api.Caching;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Messaging;
using Tadka.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Observability (ADR-040): gated on OTEL_EXPORTER_OTLP_ENDPOINT.
builder.AddTadkaTelemetry("Tadka.Restaurant.Api");

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Database-per-service (ADR-036/026): the Restaurant service owns its OWN PostgreSQL.
// Transient-fault retry (ADR-064): OFF by default; ON for the cloud failover demo. The Outbox relay's
// explicit transaction runs inside CreateExecutionStrategy().ExecuteAsync so this strategy can replay it.
var dbRetry = builder.Configuration.GetValue("Database:EnableRetryOnFailure", false);
builder.Services.AddDbContext<RestaurantDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("RestaurantDb"), npgsql =>
    {
        if (dbRetry)
            npgsql.EnableRetryOnFailure(
                builder.Configuration.GetValue("Database:MaxRetryCount", 6),
                TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MaxRetryDelaySeconds", 30)),
                null);
    }));

// Redis cache-aside (ADR-018/019) moved WITH the read-heavy service (ADR-036). Optional → no-op without Redis.
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis))
{
    // AbortOnConnectFail=false (explicit, ADR-064): keep reconnecting through a Redis restart/failover.
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    {
        var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redis);
        redisOptions.AbortOnConnectFail = false;
        return StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions);
    });
    builder.Services.AddSingleton<ICacheService, RedisCacheService>();
}
else
{
    builder.Services.AddSingleton<ICacheService, NullCacheService>();
}

// Demo levers (ADR-061 canary buggy + ADR-062 accept/reject).
builder.Services.Configure<RestaurantOptions>(builder.Configuration.GetSection(RestaurantOptions.SectionName));

// Kafka (ADR-027): publish menu-updated / restaurant-response via the Outbox → relay.
// Consume order-confirmed for accept/reject (ADR-062). OFF when unconfigured (tests / single-process dev).
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
var kafka = builder.Configuration.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>();
if (kafka?.Enabled == true)
{
    builder.Services.AddSingleton<KafkaProducer>();
    builder.Services.AddHostedService<OutboxRelay>();
    builder.Services.AddHostedService<OrderConfirmedConsumer>();
}

// Per-service JWT validation (ADR-031, defense in depth): this service verifies the SAME token the monolith
// issued, with no shared secret (ADR-067): it fetches Tadka.Api's PUBLIC keys over HTTP (JWKS) and caches them
// briefly. The network is not a trust boundary, so a direct call to Restaurant needs a valid token.
builder.Services.Configure<JwksOptions>(builder.Configuration.GetSection(JwksOptions.SectionName));
var jwksOptions = builder.Configuration.GetSection(JwksOptions.SectionName).Get<JwksOptions>() ?? new();
builder.Services.AddHttpClient(JwksClient.HttpClientName, client => client.BaseAddress = new Uri(jwksOptions.JwksBaseUrl));
builder.Services.AddSingleton<JwksClient>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // Keep our own claim names ("role"/"sub"): don't remap them to the long WS-* URIs, or
    // [Authorize(Roles = ...)] would never see the role claim for a REAL token (ADR-031, per-service validation).
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tadka",
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tadka",
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true, RoleClaimType = "role", NameClaimType = "sub"
        // IssuerSigningKeyResolver is wired below: it needs DI (IHttpClientFactory) that isn't available yet here.
    };
});
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwksClient>((options, jwksClient) =>
    {
        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) =>
        {
            if (string.IsNullOrEmpty(kid)) return [];
            // Blocking on purpose: IssuerSigningKeyResolver is a synchronous callback. The in-memory cache
            // means this almost always returns instantly without an actual HTTP call.
            var key = jwksClient.ResolveAsync(kid, CancellationToken.None).GetAwaiter().GetResult();
            return key is null ? [] : new SecurityKey[] { key };
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<RestaurantDbContext>().Database.Migrate();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => { o.Title = "Tadka Restaurant Service"; o.Theme = ScalarTheme.DeepSpace; });
}

app.UseTadkaProblemDetails();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "restaurant" }));
app.MapGet("/health/ready", async (RestaurantDbContext db) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Ok(new { status = "Ready", service = "restaurant", database = "Connected" });
    }
    catch
    {
        return ProblemDetailsExtensions.ServiceUnavailableProblem("Restaurant database is unreachable.");
    }
});
app.MapControllers();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
