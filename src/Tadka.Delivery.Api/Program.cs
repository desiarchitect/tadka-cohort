using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Tadka.Delivery.Api;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;
using Tadka.Delivery.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Database-per-service (ADR-033/026): the Delivery service owns its OWN PostgreSQL.
builder.Services.AddDbContext<DeliveryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DeliveryDb")));
builder.Services.AddScoped<DeliveryService>();

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

// Per-service JWT validation (ADR-031, defense in depth — same key as the monolith).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tadka",
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tadka",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SigningKey"] ?? "")),
        ValidateLifetime = true, RoleClaimType = "role", NameClaimType = "sub"
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

// The assigned rider posts their live position → Redis GEOADD (ADR-034), and — while actively on this
// delivery — a ping onto the customer's SSE live-tracking stream via the Day-6 backplane (ADR-020/036).
app.MapPut("/api/v1/deliveries/{orderId:guid}/location", async (Guid orderId, LocationRequest req, DeliveryDbContext db, ILocationStore loc, IOrderTrackingPublisher tracking) =>
{
    var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.OrderId == orderId);
    if (assignment is null) return Results.NotFound();
    if (!loc.Enabled) return Results.StatusCode(503); // Redis not configured
    await loc.SetAsync(assignment.AgentId, req.Latitude, req.Longitude);

    // Only push a live ping while the agent is actively on this delivery — no point publishing for an
    // assignment that's already Delivered/Cancelled; nobody's SSE stream cares anymore.
    if (tracking.Enabled && assignment.Status is AssignmentStatus.Assigned or AssignmentStatus.PickedUp)
        await tracking.PublishLocationAsync(orderId, req.Latitude, req.Longitude);

    return Results.NoContent();
}).RequireAuthorization();

// The customer tracks the order → assignment + rider + live location.
app.MapGet("/api/v1/deliveries/{orderId:guid}/track", async (Guid orderId, DeliveryDbContext db, ILocationStore loc) =>
{
    var a = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId);
    if (a is null) return Results.NotFound();
    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == a.AgentId);
    var pos = await loc.GetAsync(a.AgentId);
    return Results.Ok(new TrackResponse(orderId, a.AgentId, agent?.Name ?? "", a.Status.ToString(),
        pos is { } p ? new LocationRequest(p.Latitude, p.Longitude) : null));
}).RequireAuthorization();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
