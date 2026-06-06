using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>Boots the real Restaurant service against a real Postgres (Testcontainers). No Redis (NullCache)
/// and no Kafka in tests — CRUD, the menu-updated Outbox write, and per-service auth are deterministic.</summary>
public class RestaurantApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:RestaurantDb", _db.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "");      // → NullCacheService
        builder.UseSetting("Kafka:BootstrapServers", "");       // → relay off; Outbox rows still written
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.Scheme;
                o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                o.DefaultChallengeScheme = TestAuthHandler.Scheme;
            });
        });
    }

    public async Task InitializeAsync() => await _db.StartAsync();
    public new async Task DisposeAsync() => await _db.DisposeAsync();
}
