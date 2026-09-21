using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Tadka.Api.Data;
using Tadka.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Field-level PII encryption (ADR-045) — configured before ANY DbContext model is built (the migration
// call below triggers that), since UserConfiguration reads FieldCipher.Enabled while building the model.
// Dev-only default key, NEVER a real secret (same spirit as the seeded "seed-not-a-real-hash" password
// hash below) — a real deployment supplies Demo:EncryptionKey from a secrets manager / KMS.
Tadka.Api.Infrastructure.Security.FieldCipher.Configure(
    builder.Configuration.GetValue("Demo:EncryptPiiAtRest", true),
    builder.Configuration["Demo:EncryptionKey"] ?? "0EIJyWPct1+0ncRmpqJXxQ8AKEviFdz8+rw8PGqxKk0=");

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

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
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.RedisCacheService>();
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Realtime.IOrderTrackingBus, Tadka.Api.Infrastructure.Realtime.RedisOrderTrackingBus>();
}
else
{
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Caching.ICacheService, Tadka.Api.Infrastructure.Caching.NullCacheService>();
    builder.Services.AddSingleton<Tadka.Api.Infrastructure.Realtime.IOrderTrackingBus, Tadka.Api.Infrastructure.Realtime.NullOrderTrackingBus>();
}

// ── Payment over KAFKA, durable (ADR-027/028/029) ───────────────────────────────────────────────
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
}

// ── Authentication & Authorization (ADR-030/031, signing switched to RS256+JWKS in ADR-049) ───────
// Stateless JWT bearer; each service verifies the token itself (defense in depth — the Payment service
// resolves the SAME public key via JWKS, ADR-049). Authorization is RBAC (the `role` claim) + resource-
// ownership checks done in the controllers (the `sub` / `restaurantId` claims).
builder.Services.Configure<Tadka.Api.Auth.JwtOptions>(builder.Configuration.GetSection(Tadka.Api.Auth.JwtOptions.SectionName));
builder.Services.Configure<Tadka.Api.Auth.AuthRateLimitOptions>(builder.Configuration.GetSection(Tadka.Api.Auth.AuthRateLimitOptions.SectionName));
builder.Services.Configure<Tadka.Api.Auth.AccountLockoutOptions>(builder.Configuration.GetSection(Tadka.Api.Auth.AccountLockoutOptions.SectionName));

// Owns the RSA signing key(s) (ADR-049). Created directly (not resolved from the container) so the exact
// same instance backs both DI (TokenService, AuthController, the JWKS endpoint) and the closure the
// AddJwtBearer resolver below captures.
var signingKeys = new Tadka.Api.Auth.SigningKeyStore();
builder.Services.AddSingleton(signingKeys);
builder.Services.AddSingleton<Tadka.Api.Auth.TokenService>();
builder.Services.AddScoped<Tadka.Api.Auth.RefreshTokenService>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<Tadka.Api.Domain.Users.User>,
    Microsoft.AspNetCore.Identity.PasswordHasher<Tadka.Api.Domain.Users.User>>();

// Fixed-window rate limiting on the auth write endpoints (ADR-047) — keyed by remote IP, 429 on trip.
var rateLimit = builder.Configuration.GetSection(Tadka.Api.Auth.AuthRateLimitOptions.SectionName)
    .Get<Tadka.Api.Auth.AuthRateLimitOptions>() ?? new();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(Tadka.Api.Auth.RateLimitPolicies.AuthWrite, httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimit.PermitLimit,
                Window = TimeSpan.FromSeconds(rateLimit.WindowSeconds),
                QueueLimit = 0 // reject immediately past the limit — a demoable 429, not a queued delay
            }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Try again shortly." }, ct);
    };
});

var jwt = builder.Configuration.GetSection(Tadka.Api.Auth.JwtOptions.SectionName).Get<Tadka.Api.Auth.JwtOptions>() ?? new();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Found during live verification of ADR-049's Admin-only rotate-signing-key endpoint (a real
        // token, not the test suite's synthetic TestAuthHandler identity, is what exposed this): the JWT
        // bearer handler silently remaps short claim names ("sub"/"role"/"email") to legacy long-form URI
        // claim types (ClaimTypes.NameIdentifier/Role/Email) UNLESS told not to. That remap made
        // `RoleClaimType = "role"` below match nothing — every real (non-test) [Authorize(Roles=...)]
        // check in this app was silently broken, masked only because every integration test authenticates
        // via TestAuthHandler's synthetic claims, never through this remap. Disabling it makes claim types
        // literal, matching what TokenService actually puts in the token.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            // Resolve by the token's `kid` against the SAME key store the JWKS endpoint publishes from
            // (ADR-049) — this process just doesn't need an HTTP hop to reach its own in-memory store,
            // it still goes through the identical kid-keyed public-key lookup every other verifier uses.
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                signingKeys.Find(kid) is { } key
                    ? [new Microsoft.IdentityModel.Tokens.RsaSecurityKey(key.Rsa) { KeyId = key.Kid }]
                    : [],
            ValidateLifetime = true,
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();

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

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Standard OIDC/JWKS discovery shape (ADR-049) — anonymous, publishes only PUBLIC keys (current +
// still-in-grace-window retired ones). Payment.Api (and anyone else who needs to verify our tokens)
// fetches this instead of holding a copy of a signing secret.
app.MapGet("/.well-known/jwks.json", (Tadka.Api.Auth.SigningKeyStore keys) =>
    Results.Ok(Tadka.Api.Auth.JwkConverter.ToDocument(keys.AllForVerification))).AllowAnonymous();

app.Run();

// Exposed so the integration test project can boot the real app via WebApplicationFactory<Program>.
public partial class Program { }
