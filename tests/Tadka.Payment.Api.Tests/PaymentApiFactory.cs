using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Boots the REAL Payment service against a REAL PostgreSQL 16 in Docker (Testcontainers) — its own
/// database (ADR-026). The service migrates it on startup. Per-test gateway behaviour is overridden with
/// <c>WithWebHostBuilder</c> (same container, fresh host config).
/// </summary>
public class PaymentApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();

    public string ConnectionString => _db.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:PaymentDb", _db.GetConnectionString());
        builder.UseEnvironment("Development");

        // The Payment HTTP endpoints now require a JWT (ADR-031, per-service validation). Use a test scheme
        // (default = Admin) so the charge/query tests pass; X-Test-NoAuth simulates an unauthenticated call.
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
