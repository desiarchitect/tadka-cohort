using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Tadka.Telemetry;

namespace Tadka.Delivery.Api.Messaging;

public static class Topics
{
    public const string OrderConfirmed = "order-confirmed";   // consumed: assign a rider
    public const string OrderConfirmedDlq = "order-confirmed.dlq";
    public const string DeliveryAssigned = "delivery-assigned"; // produced: a rider took the order
}

/// <summary>Consumed from Ordering (via the Outbox) when an order is confirmed — carries what Delivery needs (no back-call).</summary>
/// <summary><see cref="RestaurantId"/> optional/additive for older messages (ADR-050).</summary>
public sealed record OrderConfirmedMessage(
    Guid MessageId, Guid OrderId, double Latitude, double Longitude, Guid RestaurantId = default);

/// <summary>Published when a rider is assigned (the 3rd participant's Saga reply).</summary>
public sealed record DeliveryAssignedMessage(Guid MessageId, Guid OrderId, Guid AgentId, string AgentName);

/// <summary>A message that failed processing repeatedly is quarantined here instead of blocking the
/// partition forever (ADR-051). <see cref="OriginalPayload"/> is the raw, unmodified JSON that failed, so an
/// operator can inspect it and, once the root cause is fixed, replay it back onto the original topic.</summary>
public sealed record DlqMessage(string OriginalTopic, string OriginalPayload, string Error, int Attempts, DateTimeOffset FailedAt);

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
            // ADR-028: bound outbox lock duration (see ADR-028 for the full trade-off).
            new ProducerConfig
            {
                BootstrapServers = options.Value.BootstrapServers,
                Acks = Acks.All,
                MessageTimeoutMs = 10_000,
                RequestTimeoutMs = 10_000
            }.ApplySasl(options.Value)).Build();

    public Task PublishAsync(string topic, string key, object payload, CancellationToken ct = default)
    {
        var message = new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(payload) };
        // Inject the consume span's traceparent so delivery-assigned stays on the order's trace (ADR-041).
        var traceParent = TadkaTrace.CurrentTraceParent();
        if (!string.IsNullOrEmpty(traceParent))
            message.Headers = new Headers { { TadkaTrace.TraceParentHeader, Encoding.UTF8.GetBytes(traceParent) } };
        return _producer.ProduceAsync(topic, message, ct);
    }

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
