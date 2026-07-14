using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Tadka.Api.Tests.Integration;

/// <summary>Boots the real Ordering monolith against a real Postgres AND a real Kafka (both Testcontainers)
/// — unlike <see cref="TadkaApiFactory"/>, which runs with Kafka effectively off for deterministic Day-4
/// order-flow assertions. This factory exists specifically to prove the poison-message/DLQ path (ADR-051)
/// and the trace-propagation fix (ADR-041) on <c>PaymentRefundedConsumer</c> against a real broker.</summary>
public class TadkaKafkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:3.8.0").Build();

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:TadkaDb", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:TadkaDbReplica", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("Payment:Mode", "Off"); // irrelevant here; keeps Day-4 order semantics out of the way
        builder.UseSetting("Kafka:BootstrapServers", BootstrapServers);
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

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _kafka.StartAsync();
        _ = Services;
    }

    public new async Task DisposeAsync()
    {
        await _kafka.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
