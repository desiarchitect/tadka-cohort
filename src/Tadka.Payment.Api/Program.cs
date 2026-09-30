using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Tadka.Payment.Api;
using Tadka.Payment.Api.Auth;
using Tadka.Payment.Api.Contracts;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;
using Tadka.Payment.Api.Gateway;
using Tadka.Payment.Api.Messaging;
using Tadka.Payment.Api.Resilience;
using Tadka.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Observability (ADR-040): same one-liner as the monolith; gated on OTEL_EXPORTER_OTLP_ENDPOINT.
builder.AddTadkaTelemetry("Tadka.Payment.Api");

// Card tokenization key (ADR-053, keyed HMAC: an unkeyed hash of a card number is reversible by brute force
// because the real entropy is tiny). Configured before anything can call Tokenize, same spirit as
// FieldCipher.Configure in Tadka.Api/Program.cs: a dev-only default, NEVER a real secret; a real deployment
// supplies Demo:CardTokenizationKey from a KMS / secrets manager, never a config file in source control.
Tadka.Payment.Api.Infrastructure.CardTokenizer.Configure(
    builder.Configuration["Demo:CardTokenizationKey"] ?? "owMbZYDfyQY0WCnoguPMpVe7Zb/voograkyID97ppuY=");

builder.Services.AddOpenApi();
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));

// Database-per-service (ADR-026): the Payment service owns its OWN PostgreSQL.
// Transient-fault retry (ADR-064): OFF by default; ON for the cloud failover demo. Explicit transactions
// run inside CreateExecutionStrategy().ExecuteAsync so the retrying strategy can replay them.
var dbRetry = builder.Configuration.GetValue("Database:EnableRetryOnFailure", false);
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PaymentDb"), npgsql =>
    {
        if (dbRetry)
            npgsql.EnableRetryOnFailure(
                builder.Configuration.GetValue("Database:MaxRetryCount", 6),
                TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MaxRetryDelaySeconds", 30)),
                null);
    }));

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
    builder.Services.AddHostedService<OutboxRelay>(); // ADR-028: payment-results / payment-refunded / DLQ
    builder.Services.AddHostedService<OrderPlacedConsumer>();
    // ADR-045: compensating refund when restaurant rejects an already-paid order.
    builder.Services.AddHostedService<RefundRequestedConsumer>();
}

// Per-service JWT validation (ADR-031, defense in depth): this service verifies the SAME token the monolith
// issued, but with no shared secret (ADR-067): it fetches Tadka.Api's PUBLIC keys over HTTP (JWKS) and caches
// them briefly. The network is not a trust boundary, so a direct call to Payment needs a valid token.
builder.Services.Configure<JwksOptions>(builder.Configuration.GetSection(JwksOptions.SectionName));
var jwksOptions = builder.Configuration.GetSection(JwksOptions.SectionName).Get<JwksOptions>() ?? new();
builder.Services.AddHttpClient(JwksClient.HttpClientName, client => client.BaseAddress = new Uri(jwksOptions.JwksBaseUrl));
builder.Services.AddSingleton<JwksClient>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // Keep claim names ("role"/"sub") as the monolith writes them. Without this the handler remaps them to
    // legacy long-form URIs, RoleClaimType below matches nothing against a REAL token, and every role check
    // silently fails (the test suite's synthetic identity never goes through the remap; see JwksValidationTests).
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tadka",
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tadka",
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        RoleClaimType = "role",
        NameClaimType = "sub"
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

app.UseTadkaProblemDetails();
app.UseAuthentication();
app.UseAuthorization();

// Liveness (process up) vs readiness (can serve — DB reachable). K8s probes map to these two.
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "payment" }));
app.MapGet("/health/ready", async (PaymentDbContext db) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Ok(new { status = "Ready", service = "payment", database = "Connected" });
    }
    catch
    {
        return ProblemDetailsExtensions.ServiceUnavailableProblem("Payment database is unreachable.");
    }
});

// Canonical public path (ADR-010): /api/v1/payments/** — same grammar as Ordering/Restaurant/Delivery.
// Charge is Admin-only (ADR-031): the REAL flow charges via the order-placed Kafka consumer, in-process, never
// over HTTP. This endpoint is a support/ops manual-charge path. Before this, ANY logged-in customer could POST
// here with someone else's orderId and an amount of their own choosing: a valid token (authentication) is not
// permission to trigger THIS action (authorization).
async Task<IResult> Charge(ChargeRequest request, PaymentService payments, CancellationToken ct)
{
    var outcome = await payments.ChargeAsync(
        request.OrderId,
        new Money(request.Amount, string.IsNullOrWhiteSpace(request.Currency) ? "INR" : request.Currency!),
        ct,
        request.CardNumber,
        request.CustomerId);
    // Decline/timeout is a BUSINESS outcome (HTTP 200 + Status=Failed); only a DOWN service throws.
    return Results.Ok(new ChargeResponse(request.OrderId, outcome.Status.ToString(), outcome.GatewayReference, outcome.FailureReason));
}

// Resource ownership (ADR-031): the customer who placed the order (carried in on the order-placed event as
// OrderPlacedMessage.CustomerId) or Admin may read it; anyone else with an otherwise-valid token gets 403.
// A payment with no CustomerId on record (a pre-fix row, or an admin charge that supplied none) is readable
// by Admin only: there is no owner to compare against, so the safe default is "no non-admin may read this".
async Task<IResult> GetPayment(Guid orderId, PaymentDbContext db, HttpContext http)
{
    var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId);
    if (p is null)
        return ProblemDetailsExtensions.NotFoundProblem("Payment", orderId);

    var isSelfOrAdmin = http.User.IsAdmin() || (p.CustomerId is { } ownerId && ownerId == http.User.UserId());
    if (!isSelfOrAdmin)
        return Results.Forbid();

    return Results.Ok(new ChargeResponse(p.OrderId, p.Status.ToString(), p.GatewayReference, p.FailureReason));
}

app.MapPost("/api/v1/payments/charge", Charge).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/v1/payments/{orderId:guid}", GetPayment).RequireAuthorization();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
