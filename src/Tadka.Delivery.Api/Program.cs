using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Tadka.Delivery.Api;
using Tadka.Delivery.Api.Auth;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;
using Tadka.Delivery.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Database-per-service (ADR-033/026): the Delivery service owns its OWN PostgreSQL.
builder.Services.AddDbContext<DeliveryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DeliveryDb")));
builder.Services.AddScoped<DeliveryService>();

// Orders that arrived when every rider was busy wait in pending_assignments; this retries them.
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection(DeliveryOptions.SectionName));
builder.Services.AddHostedService<PendingAssignmentSweeper>();

// Live location → Redis-geo (ADR-034), optional. No Redis configured ⇒ no-op (tests stay Redis-free).
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ => StackExchange.Redis.ConnectionMultiplexer.Connect(redis));
    builder.Services.AddSingleton<ILocationStore, RedisLocationStore>();
    // Same Redis connection publishes rider-location pings onto Day 6's live-tracking backplane (ADR-020/036).
    builder.Services.AddSingleton<IOrderTrackingPublisher, RedisOrderTrackingPublisher>();
}
else
{
    builder.Services.AddSingleton<ILocationStore, NullLocationStore>();
    builder.Services.AddSingleton<IOrderTrackingPublisher, NullOrderTrackingPublisher>();
}

// Kafka (ADR-027): consume order-confirmed → assign a rider → publish delivery-assigned. OFF when unconfigured.
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
var kafka = builder.Configuration.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>();
if (kafka?.Enabled == true)
{
    builder.Services.AddSingleton<KafkaProducer>();
    builder.Services.AddHostedService<OrderConfirmedConsumer>();
}

// Per-service JWT validation (ADR-031, defense in depth): this service verifies the SAME token the monolith
// issued, with no shared secret (ADR-065): it fetches Tadka.Api's PUBLIC keys over HTTP (JWKS) and caches them
// briefly. The network is not a trust boundary, so a direct call to Delivery needs a valid token.
builder.Services.Configure<JwksOptions>(builder.Configuration.GetSection(JwksOptions.SectionName));
var jwksOptions = builder.Configuration.GetSection(JwksOptions.SectionName).Get<JwksOptions>() ?? new();
builder.Services.AddHttpClient(JwksClient.HttpClientName, client => client.BaseAddress = new Uri(jwksOptions.JwksBaseUrl));
builder.Services.AddSingleton<JwksClient>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // Keep claim types exactly as the monolith's TokenService writes them ("sub", "role"). Without this the
    // handler renames them to long legacy URIs, RoleClaimType = "role" below matches nothing, and every real
    // IsInRole(...) check silently fails for a real token (the test suite's TestAuthHandler never goes through
    // that renaming, which is why this bug hides from tests; see RealJwtAuthorizationTests).
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
    scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Database.Migrate();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => { o.Title = "Tadka Delivery Service"; o.Theme = ScalarTheme.DeepSpace; });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "delivery" })); // public

// Resource ownership (ADR-031), per endpoint, in THIS service. [Authorize] / RequireAuthorization only proves
// who the caller is; these helpers decide whether this delivery is theirs. Before this, any logged-in customer
// could read any order's rider + live GPS, or move a rider's dot on someone else's map.
static bool IsAssignedRider(ClaimsPrincipal user, DeliveryAgent? agent)
    => user.IsRider() && agent?.UserId is { } riderUser && riderUser == user.UserId();

// The assigned rider posts their live position → Redis GEOADD (ADR-034), and — while actively on this
// delivery — a ping onto the customer's SSE live-tracking stream via the Day-6 backplane (ADR-020/036).
// Only the rider on THIS order (or Admin/ops) may move the dot. A customer, or a different rider, gets 403.
app.MapPut("/api/v1/deliveries/{orderId:guid}/location", async (Guid orderId, LocationRequest req, ClaimsPrincipal user, DeliveryDbContext db, ILocationStore loc, IOrderTrackingPublisher tracking) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.OrderId == orderId);
    if (assignment is null) return Results.NotFound();
    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignment.AgentId);
    if (!user.IsAdmin() && !IsAssignedRider(user, agent)) return Results.Forbid();

    if (!loc.Enabled) return Results.StatusCode(503); // Redis not configured
    await loc.SetAsync(assignment.AgentId, req.Latitude, req.Longitude);

    // Only push a live ping while the agent is actively on this delivery — no point publishing for an
    // assignment that's already Delivered/Cancelled; nobody's SSE stream cares anymore.
    if (tracking.Enabled && assignment.Status is AssignmentStatus.Assigned or AssignmentStatus.PickedUp)
        await tracking.PublishLocationAsync(orderId, req.Latitude, req.Longitude);

    return Results.NoContent();
}).RequireAuthorization();

// The customer tracks the order → assignment + rider + live location. Readable by the customer who placed
// the order (CustomerId, carried in on order-confirmed), the rider on it, or Admin. Anyone else: 403.
// An assignment with no CustomerId on record (made before this field existed) has no owner to compare
// against, so the safe default is "no customer may read it", not "every customer may".
app.MapGet("/api/v1/deliveries/{orderId:guid}/track", async (Guid orderId, ClaimsPrincipal user, DeliveryDbContext db, ILocationStore loc) =>
{
    var a = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId);
    if (a is null) return Results.NotFound();
    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == a.AgentId);

    var isOwner = a.CustomerId is { } owner && owner == user.UserId();
    if (!user.IsAdmin() && !isOwner && !IsAssignedRider(user, agent)) return Results.Forbid();

    var pos = await loc.GetAsync(a.AgentId);
    return Results.Ok(new TrackResponse(orderId, a.AgentId, agent?.Name ?? "", a.Status.ToString(),
        pos is { } p ? new LocationRequest(p.Latitude, p.Longitude) : null));
}).RequireAuthorization();

// The rider moves the delivery forward: PickedUp → Delivered (or Cancelled). Delivered/Cancelled release the
// rider back to Available, which is what lets a waiting (pending) order finally get them.
app.MapPatch("/api/v1/deliveries/{orderId:guid}/status", async (Guid orderId, DeliveryStatusRequest req, ClaimsPrincipal user, DeliveryDbContext db, DeliveryService delivery) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.OrderId == orderId);
    if (assignment is null) return Results.NotFound();
    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignment.AgentId);
    if (!user.IsAdmin() && !IsAssignedRider(user, agent)) return Results.Forbid();

    if (!Enum.TryParse<AssignmentStatus>(req.Status, ignoreCase: true, out var next) || next == AssignmentStatus.Assigned)
        return Results.Problem(detail: $"Status must be one of: PickedUp, Delivered, Cancelled.", statusCode: StatusCodes.Status400BadRequest);

    var (outcome, error) = await delivery.ChangeStatusAsync(orderId, next);
    return outcome switch
    {
        StatusChangeOutcome.Ok => Results.NoContent(),
        StatusChangeOutcome.NotFound => Results.NotFound(),
        _ => Results.Problem(detail: error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid Delivery Transition")
    };
}).RequireAuthorization();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
