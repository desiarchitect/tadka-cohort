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

// ── Payment module (ADR-021/022/023) ───────────────────────────────────────────────────────────
// Its own DbContext + schema + migration history; today it shares the same physical Postgres (logical
// separation now, physical split at Day-8 extraction). Ordering has zero references to any of this.
builder.Services.Configure<Tadka.Api.Modules.Payments.PaymentOptions>(
    builder.Configuration.GetSection(Tadka.Api.Modules.Payments.PaymentOptions.SectionName));

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("TadkaDb"),
        // The module owns its OWN migration history, in its OWN schema — independent of the core
        // context's history. This is what lets Payment's schema evolve on its own (ADR-022).
        npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "payment")));

builder.Services.AddSingleton<Tadka.Api.Modules.Payments.IPaymentGateway, Tadka.Api.Modules.Payments.FakePaymentGateway>();
builder.Services.AddSingleton<Tadka.Api.Infrastructure.Resilience.PaymentResiliencePipeline>();
builder.Services.AddSingleton<Tadka.Api.Modules.Payments.PaymentWorkChannel>();
builder.Services.AddScoped<Tadka.Api.Modules.Payments.PaymentService>();
builder.Services.AddHostedService<Tadka.Api.Modules.Payments.PaymentProcessor>();

var app = builder.Build();

// Automatically apply migrations on startup (great for cohort local dev). Each context owns its own
// migration history, so we migrate both — core first, then the Payment module's schema.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<TadkaDbContext>().Database.Migrate();
    scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.Migrate();
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
