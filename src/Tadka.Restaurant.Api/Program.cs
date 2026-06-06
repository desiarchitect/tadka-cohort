using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Tadka.Restaurant.Api.Caching;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Database-per-service (ADR-036/026): the Restaurant service owns its OWN PostgreSQL.
builder.Services.AddDbContext<RestaurantDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("RestaurantDb")));

// Redis cache-aside (ADR-018/019) moved WITH the read-heavy service (ADR-036). Optional → no-op without Redis.
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ => StackExchange.Redis.ConnectionMultiplexer.Connect(redis));
    builder.Services.AddSingleton<ICacheService, RedisCacheService>();
}
else
{
    builder.Services.AddSingleton<ICacheService, NullCacheService>();
}

// Kafka (ADR-027): publish menu-updated via the Outbox → relay. OFF when unconfigured (tests / single-process dev).
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
var kafka = builder.Configuration.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>();
if (kafka?.Enabled == true)
{
    builder.Services.AddSingleton<KafkaProducer>();
    builder.Services.AddHostedService<OutboxRelay>();
}

// Per-service JWT validation (ADR-031, defense in depth — same key as the monolith).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // Keep our own claim names ("role"/"sub") — don't remap them to the long WS-* URIs, or
    // [Authorize(Roles = …)] would never see the role claim (ADR-031, per-service validation).
    options.MapInboundClaims = false;
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
    scope.ServiceProvider.GetRequiredService<RestaurantDbContext>().Database.Migrate();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => { o.Title = "Tadka Restaurant Service"; o.Theme = ScalarTheme.DeepSpace; });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "restaurant" })); // public
app.MapControllers();

app.Run();

// Exposed so the integration test project can boot the real service via WebApplicationFactory<Program>.
public partial class Program { }
