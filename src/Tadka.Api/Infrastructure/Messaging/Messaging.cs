namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>Kafka topic names — the cross-service event contract (ADR-027). Each service owns its own copy.</summary>
public static class Topics
{
    public const string OrderPlaced = "order-placed";
    public const string PaymentResults = "payment-results";
}

/// <summary>Published by Ordering (via the Outbox) when an order is placed. Consumed by the Payment service.
/// <see cref="CustomerId"/> is event metadata, not a credential (ADR-031) — it lets Payment enforce resource
/// ownership on its OWN read endpoint without holding a copy of the orders table. Defaulted so older,
/// already-serialized outbox rows without this field still deserialize.</summary>
public sealed record OrderPlacedMessage(Guid MessageId, Guid OrderId, decimal Amount, string Currency, Guid? CustomerId = null);

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
