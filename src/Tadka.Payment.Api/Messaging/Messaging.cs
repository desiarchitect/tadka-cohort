namespace Tadka.Payment.Api.Messaging;

/// <summary>Kafka topic names — the Payment service's own copy of the contract (no shared code, ADR-027).</summary>
public static class Topics
{
    public const string OrderPlaced = "order-placed";
    public const string OrderPlacedDlq = "order-placed.dlq";
    public const string PaymentResults = "payment-results";
}

/// <summary>Consumed from Ordering: an order to charge. <see cref="Version"/> is the envelope version
/// (ADR-050) — schema evolution here is additive-only, never a rename/removal of an existing field.</summary>
public sealed record OrderPlacedMessage(Guid MessageId, Guid OrderId, decimal Amount, string Currency, int Version = 1);

/// <summary>Published back to Ordering after settling the charge (the Saga reply).</summary>
public sealed record PaymentResultMessage(Guid MessageId, Guid OrderId, string Status, string? GatewayReference, string? FailureReason);

/// <summary>A message that failed processing repeatedly is quarantined here instead of blocking the
/// partition forever (ADR-051). <see cref="OriginalPayload"/> is the raw, unmodified JSON that failed, so an
/// operator can inspect it and, once the root cause is fixed, replay it back onto <see cref="Topics.OrderPlaced"/>
/// (<c>scripts/replay-dlq.ps1</c>).</summary>
public sealed record DlqMessage(string OriginalTopic, string OriginalPayload, string Error, int Attempts, DateTimeOffset FailedAt);

/// <summary>Kafka config (ADR-027). Empty <see cref="BootstrapServers"/> ⇒ Kafka OFF (the consumer doesn't start).</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string? BootstrapServers { get; set; }
    public string ConsumerGroup { get; set; } = "tadka-payment";
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}
