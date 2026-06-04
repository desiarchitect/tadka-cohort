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

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the integration test project can boot the real app via WebApplicationFactory<Program>.
public partial class Program { }
