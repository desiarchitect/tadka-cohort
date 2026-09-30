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
            new ProducerConfig
            {
                BootstrapServers = options.Value.BootstrapServers,
                Acks = Acks.All,
                // ADR-028: bound how long a stuck broker can hold the outbox row's
                // "FOR UPDATE SKIP LOCKED" transaction open. Without this, librdkafka's
                // default MessageTimeoutMs (300s) applies and the lock is held that long
                // when Kafka is unreachable. Kept short and paired with the outbox retry loop.
                MessageTimeoutMs = 10_000,
                RequestTimeoutMs = 10_000
            }.ApplySasl(options.Value)).Build();
    }

    public Task PublishAsync(string topic, string key, object payload, CancellationToken cancellationToken = default)
    {
        var value = JsonSerializer.Serialize(payload);
        var traceParent = TadkaTrace.CurrentTraceParent();
        return PublishRawAsync(topic, key, value, traceParent, cancellationToken);
    }

    /// <summary>Publish a pre-serialized outbox payload, re-injecting the stored traceparent (ADR-041).</summary>
    public Task PublishRawAsync(string topic, string key, string value, string? traceParent = null, CancellationToken cancellationToken = default)
    {
        var message = new Message<string, string> { Key = key, Value = value };
        if (!string.IsNullOrEmpty(traceParent))
            message.Headers = new Headers { { TadkaTrace.TraceParentHeader, Encoding.UTF8.GetBytes(traceParent) } };
        return _producer.ProduceAsync(topic, message, cancellationToken);
    }

    public void Dispose() => _producer.Dispose();
}
