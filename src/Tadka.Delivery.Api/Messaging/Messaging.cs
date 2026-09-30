using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Tadka.Delivery.Api.Messaging;

public static class Topics
{
    public const string OrderConfirmed = "order-confirmed";   // consumed: assign a rider
    public const string DeliveryAssigned = "delivery-assigned"; // produced: a rider took the order
}

/// <summary>Consumed from Ordering (via the Outbox) when an order is confirmed — carries what Delivery needs (no back-call).</summary>
public sealed record OrderConfirmedMessage(Guid MessageId, Guid OrderId, double Latitude, double Longitude);

/// <summary>Published when a rider is assigned (the 3rd participant's Saga reply).</summary>
public sealed record DeliveryAssignedMessage(Guid MessageId, Guid OrderId, Guid AgentId, string AgentName);

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string? BootstrapServers { get; set; }
    public string ConsumerGroup { get; set; } = "tadka-delivery";
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
    public bool SaslEnabled => !string.IsNullOrWhiteSpace(SaslUsername);
}

/// <summary>Thin singleton Kafka producer for <c>delivery-assigned</c> (ADR-027).</summary>
public sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
        => _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.Value.BootstrapServers, Acks = Acks.All }.ApplySasl(options.Value)).Build();

    public Task PublishAsync(string topic, string key, object payload, CancellationToken ct = default)
        => _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(payload) }, ct);

    public void Dispose() => _producer.Dispose();
}

/// <summary>Kafka client authentication (SASL/SCRAM-SHA-256, ADR-027 security addendum). Applied to every
/// producer and consumer config in this service. A no-op unless <c>Kafka:SaslUsername</c> is set, so the
/// test suite (Testcontainers Kafka, no auth) and an un-secured broker keep working unchanged.</summary>
public static class KafkaSecurity
{
    public static T ApplySasl<T>(this T config, KafkaOptions options) where T : ClientConfig
    {
        if (!options.SaslEnabled) return config;
        config.SecurityProtocol = SecurityProtocol.SaslPlaintext;
        config.SaslMechanism = SaslMechanism.ScramSha256;
        config.SaslUsername = options.SaslUsername;
        config.SaslPassword = options.SaslPassword;
        return config;
    }
}
