using Confluent.Kafka;
using Tadka.Api.Infrastructure.Messaging;

namespace Tadka.Api.Tests;

/// <summary>
/// Pure unit tests for the Kafka client authentication helper (ADR-027 security addendum): no broker and
/// no Docker, matching the project's "tests stay green without Kafka" rule. The integration tests'
/// Testcontainers Kafka never sets credentials, so they exercise the "disabled" path unchanged.
/// </summary>
public class KafkaSecurityTests
{
    [Fact]
    public void Configured_credentials_turn_on_SASL_SCRAM_for_producers_and_consumers()
    {
        var options = new KafkaOptions { BootstrapServers = "localhost:9092", SaslUsername = "tadka", SaslPassword = "secret" };

        ClientConfig producer = new ProducerConfig { BootstrapServers = options.BootstrapServers }.ApplySasl(options);
        ClientConfig consumer = new ConsumerConfig { BootstrapServers = options.BootstrapServers, GroupId = "g" }.ApplySasl(options);

        foreach (var config in new[] { producer, consumer })
        {
            Assert.Equal(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
            Assert.Equal(SaslMechanism.ScramSha256, config.SaslMechanism);
            Assert.Equal("tadka", config.SaslUsername);
            Assert.Equal("secret", config.SaslPassword);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_username_the_config_is_left_exactly_as_it_was(string? username)
    {
        var options = new KafkaOptions { BootstrapServers = "localhost:9092", SaslUsername = username, SaslPassword = "ignored" };

        var config = new ConsumerConfig { BootstrapServers = options.BootstrapServers, GroupId = "g" }.ApplySasl(options);

        Assert.False(options.SaslEnabled);
        Assert.Null(config.SecurityProtocol);
        Assert.Null(config.SaslMechanism);
        Assert.Null(config.SaslUsername);
        Assert.Null(config.SaslPassword);
    }
}
