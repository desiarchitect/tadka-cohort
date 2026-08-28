using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Boots the REAL Tadka API against a REAL PostgreSQL 16 in Docker (Testcontainers) — not an
/// in-memory provider. Schema-per-domain, TIMESTAMPTZ, gen_random_uuid(), and the xmin
/// concurrency token only behave correctly on real Postgres, so that is what we test against
/// (ADR-012, Day-4 testing decision). The app's startup migration creates the schema + seeds.
/// </summary>
public class TadkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Point the app at the throwaway container instead of the developer's local DB.
        builder.UseSetting("ConnectionStrings:TadkaDb", _postgres.GetConnectionString());
        builder.UseEnvironment("Development");
    }

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public new async Task DisposeAsync() => await _postgres.DisposeAsync();
}
