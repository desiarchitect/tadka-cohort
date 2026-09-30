using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Tadka.Delivery.Api.Tests;

/// <summary>Boots the real Delivery service against a real Postgres (Testcontainers). No real Redis and no
/// Kafka in tests: assignment logic + per-service auth are tested deterministically. The Redis-backed
/// ILocationStore is swapped for an in-memory fake (<see cref="FakeLocationStore"/>) rather than left as the
/// no-op Null implementation, so the location endpoint can be driven past its "Redis not configured" guard.</summary>
public class DeliveryApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();

    /// <summary>True: requests authenticate through <see cref="TestAuthHandler"/> (X-Test-* headers). False:
    /// the service's REAL JWT bearer validation runs, so tests must send real signed tokens
    /// (<see cref="RealJwtDeliveryApiFactory"/>), the only way to catch claim-mapping bugs.</summary>
    protected virtual bool UseTestAuth => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DeliveryDb", _db.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "");   // → NullLocationStore (replaced by the fake below)
        builder.UseSetting("Kafka:BootstrapServers", "");    // no broker in tests → no consumer
        builder.UseSetting("Delivery:PendingRetrySeconds", "0"); // tests drive RetryPendingAsync directly
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            if (UseTestAuth)
            {
                services.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultScheme = TestAuthHandler.Scheme;
                    o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    o.DefaultChallengeScheme = TestAuthHandler.Scheme;
                });
            }

            services.AddSingleton<ILocationStore, FakeLocationStore>();
        });
    }

    public async Task InitializeAsync() => await _db.StartAsync();
    public new async Task DisposeAsync() => await _db.DisposeAsync();
}

/// <summary>
/// Same real service + Postgres, but with the service's own JWT bearer + <see cref="Tadka.Delivery.Api.Auth.JwksClient"/>
/// pipeline left in place (ADR-067), exactly as Program.cs wires it. The "jwks" HttpClient is redirected to a
/// <see cref="FakeJwksServer"/> and the cache TTL forced to 0, so a test signs real RS256 tokens with a key the fake
/// server publishes and every resolution reflects the current key set.
/// </summary>
public class RealJwtDeliveryApiFactory : DeliveryApiFactory
{
    protected override bool UseTestAuth => false;

    public FakeJwksServer Jwks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:JwksCacheMinutes", "0");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(Tadka.Delivery.Api.Auth.JwksClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Jwks.CreateHandler());
        });
    }
}
