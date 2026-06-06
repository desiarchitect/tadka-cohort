using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Tadka.Telemetry;

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
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Thin singleton Kafka producer for <c>delivery-assigned</c> (ADR-027).</summary>
public sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
        => _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.Value.BootstrapServers, Acks = Acks.All }).Build();

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
