using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Tadka.Delivery.Api.Tests;

/// <summary>Boots the real Delivery service against a real Postgres AND a real Kafka (both Testcontainers) —
/// unlike <see cref="DeliveryApiFactory"/>, which runs with Kafka off for deterministic tests. This factory
/// exists specifically to prove the poison-message/DLQ path (ADR-051, the review fix that added it to
/// <c>OrderConfirmedConsumer</c>) against a real broker, not a mock.</summary>
public class DeliveryKafkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16").Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:3.8.0").Build();

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DeliveryDb", _db.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "");   // → NullLocationStore
        builder.UseSetting("Kafka:BootstrapServers", BootstrapServers);
        // The Testcontainers broker is unauthenticated, but Development appsettings carry the compose
        // broker's SASL credentials: blank the username so the test host does not try to authenticate.
        builder.UseSetting("Kafka:SaslUsername", "");
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
        await _db.StartAsync();
        await _kafka.StartAsync();
        _ = Services;
    }

    public new async Task DisposeAsync()
    {
        await _kafka.DisposeAsync();
        await _db.DisposeAsync();
    }
}
