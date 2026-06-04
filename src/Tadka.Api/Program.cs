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

// ── Payment, now a SEPARATE service (ADR-024/025/026) ───────────────────────────────────────────
// Day 8: the Payment module was extracted into Tadka.Payment.Api (own process + own database). The
// monolith no longer contains the gateway, PaymentService, or PaymentDbContext — it keeps only the async
// orchestration (queue + background processor) and a typed HTTP client to the Payment service, wrapped in
// the REUSED Day-7 Polly pipeline (timeout + bulkhead, now around the network hop). POST /orders is still
// decoupled from payment via the in-process queue, so intake stays in milliseconds (ADR-023, preserved).
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

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the integration test project can boot the real app via WebApplicationFactory<Program>.
public partial class Program { }
