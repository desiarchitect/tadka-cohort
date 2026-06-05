using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Tadka.Payment.Api;
using Tadka.Payment.Api.Contracts;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;
using Tadka.Payment.Api.Gateway;
using Tadka.Payment.Api.Messaging;
using Tadka.Payment.Api.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));

// Database-per-service (ADR-026): the Payment service owns its OWN PostgreSQL.
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PaymentDb")));

builder.Services.AddSingleton<IPaymentGateway, FakePaymentGateway>();
builder.Services.AddSingleton<PaymentResiliencePipeline>();
builder.Services.AddScoped<PaymentService>();

// Kafka (ADR-027): consume `order-placed`, charge, publish `payment-results` (the Saga reply). OFF when
// no BootstrapServers are configured (the HTTP charge endpoint + tests still work without a broker).
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
var kafkaOptions = builder.Configuration.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>();
if (kafkaOptions?.Enabled == true)
{
    builder.Services.AddSingleton<KafkaProducer>();
    builder.Services.AddHostedService<OrderPlacedConsumer>();
}

// Per-service JWT validation (ADR-031, defense in depth): this service verifies the SAME token with the
// SAME signing key as the monolith. The network is not a trust boundary — even with no gateway, a direct
// call to the Payment service's HTTP endpoints needs a valid token.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tadka",
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tadka",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SigningKey"] ?? "")),
        ValidateLifetime = true,
        RoleClaimType = "role",
        NameClaimType = "sub"
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();

// The service owns its data: it migrates its OWN database on startup. If THIS database is down, only the
// Payment service fails to start — the monolith (its own DB) is unaffected (ADR-024/026).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Tadka Payment Service";
        options.Theme = ScalarTheme.DeepSpace;
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "payment" })); // public

// POST /payments/charge — idempotent by orderId (ADR-025). A decline/timeout is a BUSINESS outcome
// (HTTP 200 with Status=Failed); only a DOWN service makes the caller's HTTP call throw.
app.MapPost("/payments/charge", async (ChargeRequest request, PaymentService payments, CancellationToken ct) =>
{
    var outcome = await payments.ChargeAsync(
        request.OrderId, new Money(request.Amount, string.IsNullOrWhiteSpace(request.Currency) ? "INR" : request.Currency!), ct);
    return Results.Ok(new ChargeResponse(request.OrderId, outcome.Status.ToString(), outcome.GatewayReference, outcome.FailureReason));
}).RequireAuthorization();

// GET /payments/{orderId} — query a payment's status (request/reply stays HTTP even after Day-9 Kafka).
app.MapGet("/payments/{orderId:guid}", async (Guid orderId, PaymentDbContext db) =>
{
    var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId);
    return p is null
        ? Results.NotFound()
        : Results.Ok(new ChargeResponse(p.OrderId, p.Status.ToString(), p.GatewayReference, p.FailureReason));
}).RequireAuthorization();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
