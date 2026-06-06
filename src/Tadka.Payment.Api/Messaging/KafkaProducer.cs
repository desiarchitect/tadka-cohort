using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Tadka.Telemetry;

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
    {
        var message = new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(payload) };
        // Inject the consume span's traceparent so payment-results stays on the order's trace (ADR-041).
        var traceParent = TadkaTrace.CurrentTraceParent();
        if (!string.IsNullOrEmpty(traceParent))
            message.Headers = new Headers { { TadkaTrace.TraceParentHeader, Encoding.UTF8.GetBytes(traceParent) } };
        return _producer.ProduceAsync(topic, message, cancellationToken);
    }

    public void Dispose() => _producer.Dispose();
}
