using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    }

    public async Task InitializeAsync() => await _db.StartAsync();

    public new async Task DisposeAsync() => await _db.DisposeAsync();
}
