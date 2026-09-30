using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Restaurant.Api.Auth;
using Testcontainers.PostgreSql;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>
/// Unlike <see cref="RestaurantApiFactory"/>, this factory does NOT swap in the test auth scheme: it keeps the
/// REAL <c>AddJwtBearer</c> + <see cref="JwksClient"/> pipeline wired exactly as Program.cs configures it
/// (ADR-067), so <see cref="JwksValidationTests"/> exercises what verifies a token in production: fetch by
/// <c>kid</c>, cache, reject on miss, and keep the <c>role</c>/<c>restaurantId</c> claims un-renamed. The "jwks"
/// HttpClient is redirected to a <see cref="FakeJwksServer"/>, and the cache TTL is forced to 0 so every
/// resolution reflects the fake server's current key set.
/// </summary>
public sealed class RealAuthRestaurantApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();

    public FakeJwksServer Jwks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:RestaurantDb", _db.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("Kafka:BootstrapServers", "");
        builder.UseSetting("Jwt:JwksCacheMinutes", "0");
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(JwksClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Jwks.CreateHandler());
        });
    }

    public async Task InitializeAsync() => await _db.StartAsync();

    public new async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Jwks.Dispose();
    }
}
