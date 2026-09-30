using Confluent.Kafka;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>Kafka topic names — the cross-service event contract (ADR-027). Each service owns its own copy.</summary>
public static class Topics
{
    public const string OrderPlaced = "order-placed";
    public const string PaymentResults = "payment-results";
    public const string OrderConfirmed = "order-confirmed";   // → Delivery assigns a rider (ADR-033)
    public const string RefundRequested = "refund-requested"; // → the restaurant rejected an already-paid order (ADR-045)
    public const string PaymentRefunded = "payment-refunded"; // ← the compensating refund settled (ADR-045)
}

/// <summary>Published by Ordering (via the Outbox) when an order is placed. Consumed by the Payment service.</summary>
public sealed record OrderPlacedMessage(Guid MessageId, Guid OrderId, decimal Amount, string Currency);

/// <summary>Published by Ordering (via the Outbox) when an order auto-confirms after payment — carries what
/// the Delivery service needs (no back-call, ADR-008). Consumed by the Delivery service.</summary>
public sealed record OrderConfirmedMessage(Guid MessageId, Guid OrderId, double Latitude, double Longitude);

/// <summary>Published by the Payment service after it settles a charge. Consumed by Ordering (the Saga reaction).</summary>
public sealed record PaymentResultMessage(Guid MessageId, Guid OrderId, string Status, string? GatewayReference, string? FailureReason);

/// <summary>Published by Ordering (via the Outbox) when the restaurant rejects an order whose payment
/// already completed — the compensating action (ADR-045). Consumed by the Payment service.</summary>
public sealed record RefundRequestedMessage(Guid MessageId, Guid OrderId, string? GatewayReference);

/// <summary>Published by the Payment service after a refund settles. Consumed by Ordering, purely to
/// surface the outcome on the live-tracking stream — the order itself is already Cancelled by this point.</summary>
public sealed record PaymentRefundedMessage(Guid MessageId, Guid OrderId, string Status);

/// <summary>Kafka config (ADR-027). If <see cref="BootstrapServers"/> is empty, Kafka is OFF — the relay and
/// consumers don't start and the producer is a no-op, so single-process dev and the test suite run unchanged.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string? BootstrapServers { get; set; }
    public string ConsumerGroup { get; set; } = "tadka-monolith";
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
    public bool SaslEnabled => !string.IsNullOrWhiteSpace(SaslUsername);
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
