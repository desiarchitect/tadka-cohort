using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Tadka.Api.Data;
using Tadka.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

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

// Server-side pricing source (ADR-037). Restaurant was extracted (ADR-036), so order pricing no longer
// reads an in-process Restaurant aggregate. Default = the local read model (available even when Restaurant
// is down). `Ordering:RestaurantReadMode = SyncHttp` swaps in a synchronous HTTP read to demonstrate the
// temporal coupling the read model avoids (the Day-12 "Restaurant down → orders still flow" demo).
var readMode = builder.Configuration["Ordering:RestaurantReadMode"] ?? "LocalReplica";
if (string.Equals(readMode, "SyncHttp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<Tadka.Api.Domain.Orders.IRestaurantPricingSource,
        Tadka.Api.Infrastructure.RestaurantReadModel.HttpRestaurantPricingSource>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:Restaurant:BaseUrl"] ?? "http://localhost:5260");
        c.Timeout = TimeSpan.FromSeconds(2); // fail fast — but a down peer still fails the order (the point)
    });
}
else
{
    builder.Services.AddScoped<Tadka.Api.Domain.Orders.IRestaurantPricingSource,
        Tadka.Api.Infrastructure.RestaurantReadModel.LocalReplicaPricingSource>();
}

// In-process events via MediatR (ADR-022, supersedes the Day-4 hand-rolled dispatcher). One call
// auto-registers every INotificationHandler<T> in the assembly — order notification + SSE backplane
// (ADR-020) + the Payment module's OrderPlaced handler + the order's reaction to payment settling.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

// Day 11 refund saga levers (ADR-045/046): restaurant accept/reject + choreography vs orchestration naming.
builder.Services.Configure<Tadka.Api.Domain.Restaurants.RestaurantAcceptanceOptions>(
    builder.Configuration.GetSection(Tadka.Api.Domain.Restaurants.RestaurantAcceptanceOptions.SectionName));
builder.Services.Configure<Tadka.Api.Infrastructure.Messaging.SagaOptions>(
    builder.Configuration.GetSection(Tadka.Api.Infrastructure.Messaging.SagaOptions.SectionName));
builder.Services.AddScoped<Tadka.Api.Infrastructure.Messaging.RefundSagaOrchestrator>();

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
    // Keep the local price replica fresh from the Restaurant service's menu-updated events (ADR-037).
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.MenuUpdatedConsumer>();
    // ADR-045: surface payment-refunded on the live-tracking bus after compensation settles.
    builder.Services.AddHostedService<Tadka.Api.Infrastructure.Messaging.PaymentRefundedConsumer>();
}

// ── Authentication & Authorization (ADR-030/031) ────────────────────────────────────────────────
// Stateless JWT bearer; each service verifies the token itself (defense in depth — the Payment service
// validates the SAME key). Authorization is RBAC (the `role` claim) + resource-ownership checks done in
// the controllers (the `sub` / `restaurantId` claims).
builder.Services.Configure<Tadka.Api.Auth.JwtOptions>(builder.Configuration.GetSection(Tadka.Api.Auth.JwtOptions.SectionName));
builder.Services.AddSingleton<Tadka.Api.Auth.TokenService>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<Tadka.Api.Domain.Users.User>,
    Microsoft.AspNetCore.Identity.PasswordHasher<Tadka.Api.Domain.Users.User>>();

var jwt = builder.Configuration.GetSection(Tadka.Api.Auth.JwtOptions.SectionName).Get<Tadka.Api.Auth.JwtOptions>() ?? new();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep our own claim names ("role"/"sub"); don't remap to the long WS-* URIs, or
        // [Authorize(Roles = …)] would never see the role claim from our JsonWebToken (ADR-030/031).
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the integration test project can boot the real app via WebApplicationFactory<Program>.
public partial class Program { }
