using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Payment.Api.Auth;
using Testcontainers.PostgreSql;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Unlike <see cref="PaymentApiFactory"/>, this factory does NOT swap in the test auth scheme — it keeps
/// the REAL <c>AddJwtBearer</c> + <see cref="JwksClient"/> pipeline wired exactly as Program.cs configures
/// it (ADR-049), so <see cref="JwksValidationTests"/> exercises the actual thing that verifies a token in
/// production: fetch-by-kid, cache, and reject-on-miss. The "jwks" named HttpClient is redirected to a
/// <see cref="FakeJwksServer"/> instead of a real network call, and the cache TTL is forced to 0 so every
/// resolution reflects the fake server's CURRENT key set (a real deployment uses a multi-minute TTL —
/// see ADR-049's Trade-off; a 0-minute TTL here trades that off for deterministic, wait-free assertions).
/// </summary>
public sealed class RealAuthPaymentApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();

    public FakeJwksServer Jwks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:PaymentDb", _db.GetConnectionString());
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
