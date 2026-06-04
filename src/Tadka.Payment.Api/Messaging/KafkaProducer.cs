using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Tadka.Payment.Api.Messaging;

/// <summary>Thin singleton Kafka producer (ADR-027) for publishing <c>payment-results</c>.</summary>
public sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.Value.BootstrapServers, Acks = Acks.All }).Build();
    }

    public Task PublishAsync(string topic, string key, object payload, CancellationToken cancellationToken = default)
        => _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(payload) }, cancellationToken);

    public void Dispose() => _producer.Dispose();
}
