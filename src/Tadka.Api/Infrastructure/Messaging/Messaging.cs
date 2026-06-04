namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>Kafka topic names — the cross-service event contract (ADR-027). Each service owns its own copy.</summary>
public static class Topics
{
    public const string OrderPlaced = "order-placed";
    public const string PaymentResults = "payment-results";
}

/// <summary>Published by Ordering (via the Outbox) when an order is placed. Consumed by the Payment service.</summary>
public sealed record OrderPlacedMessage(Guid MessageId, Guid OrderId, decimal Amount, string Currency);

/// <summary>Published by the Payment service after it settles a charge. Consumed by Ordering (the Saga reaction).</summary>
public sealed record PaymentResultMessage(Guid MessageId, Guid OrderId, string Status, string? GatewayReference, string? FailureReason);

/// <summary>Kafka config (ADR-027). If <see cref="BootstrapServers"/> is empty, Kafka is OFF — the relay and
/// consumers don't start and the producer is a no-op, so single-process dev and the test suite run unchanged.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string? BootstrapServers { get; set; }
    public string ConsumerGroup { get; set; } = "tadka-monolith";
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}
