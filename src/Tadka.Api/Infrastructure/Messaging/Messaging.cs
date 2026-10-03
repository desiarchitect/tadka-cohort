using Confluent.Kafka;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>Kafka topic names — the cross-service event contract (ADR-027). Each service owns its own copy.</summary>
public static class Topics
{
    public const string OrderPlaced = "order-placed";
    public const string PaymentResults = "payment-results";
    public const string PaymentResultsDlq = "payment-results.dlq";
    public const string OrderConfirmed = "order-confirmed";   // → Delivery assigns a rider (ADR-033)
    public const string MenuUpdated = "menu-updated";         // ← Restaurant publishes; Ordering updates its price replica (ADR-037)
    public const string MenuUpdatedDlq = "menu-updated.dlq";
    public const string RefundRequested = "refund-requested"; // → the restaurant rejected an already-paid order (ADR-045)
    public const string PaymentRefunded = "payment-refunded"; // ← the compensating refund settled (ADR-045)
    public const string PaymentRefundedDlq = "payment-refunded.dlq";
    public const string RestaurantResponse = "restaurant-response"; // ← Restaurant.Api accept/reject (ADR-062)
    public const string RestaurantResponseDlq = "restaurant-response.dlq";
}

/// <summary>One menu item, as carried in a <see cref="RestaurantSnapshotMessage"/> (ADR-037).</summary>
public sealed record MenuItemSnapshot(
    Guid MenuItemId, string Name, decimal PriceAmount, string PriceCurrency, bool IsAvailable, string Category, bool IsVeg);

public sealed record AddressSnapshot(
    string Line1, string Line2, string City, string Pincode, double Latitude, double Longitude);

/// <summary>The full state of a restaurant + menu at a change (ADR-037, event-carried state transfer).
/// Consumed by Ordering to upsert its local price replica — no back-call (ADR-008).</summary>
public sealed record RestaurantSnapshotMessage(
    Guid MessageId, Guid RestaurantId, string Name, bool IsActive, AddressSnapshot Address, List<MenuItemSnapshot> Menu);

/// <summary>Published by Ordering (via the Outbox) when an order is placed. Consumed by the Payment service.
/// <see cref="Version"/> is the envelope version (ADR-050): schema evolution is additive-only.
/// <see cref="CustomerId"/> is additive too: Payment stores it so it can answer "is this YOUR payment?" (ADR-031).</summary>
public sealed record OrderPlacedMessage(Guid MessageId, Guid OrderId, decimal Amount, string Currency, int Version = 1, Guid? CustomerId = null);

/// <summary>Published by Ordering (via the Outbox) when an order auto-confirms after payment — carries what
/// consumers need without a back-call (ADR-008). Consumed by Delivery (rider) and Restaurant (accept/reject, ADR-062).
/// <see cref="RestaurantId"/> and <see cref="CustomerId"/> are additive (ADR-050): older producers omit them; consumers
/// treat default/null as unknown. Delivery keeps CustomerId so it can tell the customer who placed an order from everyone else (ADR-031).</summary>
public sealed record OrderConfirmedMessage(
    Guid MessageId, Guid OrderId, double Latitude, double Longitude, Guid RestaurantId = default, Guid? CustomerId = null);

/// <summary>Published by the Payment service after it settles a charge. Consumed by Ordering (the Saga reaction).</summary>
public sealed record PaymentResultMessage(Guid MessageId, Guid OrderId, string Status, string? GatewayReference, string? FailureReason);

/// <summary>Published by Ordering (via the Outbox) when the restaurant rejects an order whose payment
/// already completed — the compensating action (ADR-045). Consumed by the Payment service.</summary>
public sealed record RefundRequestedMessage(Guid MessageId, Guid OrderId, string? GatewayReference);

/// <summary>Published by the Payment service after a refund settles. Consumed by Ordering, purely to
/// surface the outcome on the live-tracking stream — the order itself is already Cancelled by this point.</summary>
public sealed record PaymentRefundedMessage(Guid MessageId, Guid OrderId, string Status);

/// <summary>Restaurant.Api decision after order-confirmed (ADR-062). Status = Accepted | Rejected.</summary>
public sealed record RestaurantResponseMessage(
    Guid MessageId, Guid OrderId, string Status, string? Reason, string? GatewayReference);

/// <summary>A message that failed processing repeatedly is quarantined here instead of blocking the
/// partition forever (ADR-051). <see cref="OriginalPayload"/> is the raw, unmodified JSON that failed, so an
/// operator can inspect it and, once the root cause is fixed, replay it back onto the original topic.</summary>
public sealed record DlqMessage(string OriginalTopic, string OriginalPayload, string Error, int Attempts, DateTimeOffset FailedAt);

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
