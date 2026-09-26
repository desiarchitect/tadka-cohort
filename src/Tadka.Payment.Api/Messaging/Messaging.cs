namespace Tadka.Payment.Api.Messaging;

/// <summary>Kafka topic names — the Payment service's own copy of the contract (no shared code, ADR-027).</summary>
public static class Topics
{
    public const string OrderPlaced = "order-placed";
    public const string PaymentResults = "payment-results";
}

/// <summary>Consumed from Ordering: an order to charge. <see cref="CustomerId"/> is event metadata, not a
/// credential (ADR-031) — it lets this service stamp resource ownership onto the <c>Payment</c> row it
/// creates, since Payment has no copy of the orders table of its own to check ownership against.</summary>
public sealed record OrderPlacedMessage(Guid MessageId, Guid OrderId, decimal Amount, string Currency, Guid? CustomerId = null);

/// <summary>Published back to Ordering after settling the charge (the Saga reply).</summary>
public sealed record PaymentResultMessage(Guid MessageId, Guid OrderId, string Status, string? GatewayReference, string? FailureReason);

/// <summary>Kafka config (ADR-027). Empty <see cref="BootstrapServers"/> ⇒ Kafka OFF (the consumer doesn't start).</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string? BootstrapServers { get; set; }
    public string ConsumerGroup { get; set; } = "tadka-payment";
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}
