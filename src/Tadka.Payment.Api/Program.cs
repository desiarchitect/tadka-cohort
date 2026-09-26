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

var builder = WebApplication.CreateBuilder(args);

// Card tokenization key (ADR-046, keyed after a live security review found the earlier unkeyed-SHA-256
// version reversible by brute force — see CardTokenizer's own doc comment). Configured before anything
// can call Tokenize, same spirit as FieldCipher.Configure in Tadka.Api/Program.cs: a dev-only default,
// NEVER a real secret — a real deployment supplies Demo:CardTokenizationKey from a KMS/secrets manager,
// never a config file in source control.
Tadka.Payment.Api.Infrastructure.CardTokenizer.Configure(
    builder.Configuration["Demo:CardTokenizationKey"] ?? "owMbZYDfyQY0WCnoguPMpVe7Zb/voograkyID97ppuY=");

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

// Per-service JWT validation (ADR-031, defense in depth): this service verifies the SAME token the
// monolith issued — but no shared secret any more (ADR-049). It fetches Tadka.Api's PUBLIC key over
// HTTP (JWKS) and caches it briefly; the network is not a trust boundary — even with no gateway, a direct
// call to the Payment service's HTTP endpoints needs a valid token.
builder.Services.Configure<JwksOptions>(builder.Configuration.GetSection(JwksOptions.SectionName));
var jwksOptions = builder.Configuration.GetSection(JwksOptions.SectionName).Get<JwksOptions>() ?? new();
builder.Services.AddHttpClient(JwksClient.HttpClientName, client => client.BaseAddress = new Uri(jwksOptions.JwksBaseUrl));
builder.Services.AddSingleton<JwksClient>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // See the matching comment in Tadka.Api/Program.cs (ADR-049 live-verification finding): without
    // this, the handler silently remaps "sub"/"role" to legacy long-form claim URIs and RoleClaimType
    // below matches nothing against a REAL token.
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tadka",
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tadka",
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        RoleClaimType = "role",
        NameClaimType = "sub"
        // IssuerSigningKeyResolver is wired below via AddOptions().Configure<JwksClient> — it needs DI
        // (IHttpClientFactory) that isn't available yet at this point in Program.cs.
    };
});
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwksClient>((options, jwksClient) =>
    {
        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) =>
        {
            if (string.IsNullOrEmpty(kid)) return [];
            // Blocking on purpose: IssuerSigningKeyResolver is a synchronous callback. The in-memory
            // cache (ADR-049) means this almost always returns instantly without an actual HTTP call.
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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "payment" })); // public

// POST /payments/charge — idempotent by orderId (ADR-025). A decline/timeout is a BUSINESS outcome
// (HTTP 200 with Status=Failed); only a DOWN service makes the caller's HTTP call throw.
// Admin-only (ADR-031): the REAL flow charges via the order-placed Kafka consumer, in-process, never
// over HTTP — this endpoint is a support/ops manual-charge path. Before this fix ANY logged-in customer
// could POST here with someone else's orderId and an amount of their own choosing; authentication alone
// (a valid token) was never the same as authorization (permission to trigger THIS action).
app.MapPost("/payments/charge", async (ChargeRequest request, PaymentService payments, CancellationToken ct) =>
{
    var outcome = await payments.ChargeAsync(
        request.OrderId, new Money(request.Amount, string.IsNullOrWhiteSpace(request.Currency) ? "INR" : request.Currency!),
        ct, request.CardNumber, request.CustomerId);
    return Results.Ok(new ChargeResponse(request.OrderId, outcome.Status.ToString(), outcome.GatewayReference, outcome.FailureReason));
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// GET /payments/{orderId} — query a payment's status (request/reply stays HTTP even after Day-9 Kafka).
// Resource ownership (ADR-031): the customer who placed the order (carried in via the order-placed event,
// see OrderPlacedMessage.CustomerId) or Admin may read it; anyone else with an otherwise-valid token gets
// 403, not the payment status of an order that isn't theirs. A payment with no CustomerId on record (a
// pre-fix row, or an admin-triggered charge that didn't supply one) is readable by Admin only — there is
// no owner to compare against, so the safe default is "no non-admin may read this", not "everyone may".
app.MapGet("/payments/{orderId:guid}", async (Guid orderId, PaymentDbContext db, HttpContext http) =>
{
    var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId);
    if (p is null)
        return Results.NotFound();

    var isSelfOrAdmin = http.User.IsAdmin() || (p.CustomerId is { } ownerId && ownerId == http.User.UserId());
    if (!isSelfOrAdmin)
        return Results.Forbid();

    return Results.Ok(new ChargeResponse(p.OrderId, p.Status.ToString(), p.GatewayReference, p.FailureReason));
}).RequireAuthorization();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
