using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// Thin singleton wrapper over a Confluent.Kafka producer (ADR-027). Hand-rolled to show the mechanics;
/// in production MassTransit/NServiceBus own the producer + outbox + retries. The OutboxRelay calls
/// <see cref="PublishRawAsync"/> with payloads already serialized by the outbox.
/// </summary>
public sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.Value.BootstrapServers, Acks = Acks.All }).Build();
    }

    public Task PublishRawAsync(string topic, string key, string value, CancellationToken cancellationToken = default)
        => _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, cancellationToken);

    public Task PublishAsync(string topic, string key, object payload, CancellationToken cancellationToken = default)
        => PublishRawAsync(topic, key, JsonSerializer.Serialize(payload), cancellationToken);

    public void Dispose() => _producer.Dispose();
}
