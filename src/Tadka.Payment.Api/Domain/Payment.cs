namespace Tadka.Payment.Api.Domain;

/// <summary>Money value object — the Payment service owns its own copy (no shared code across services).</summary>
public record Money(decimal Amount, string Currency = "INR");

/// <summary>
/// A payment attempt for an order. This service OWNS this type and its data (ADR-024/026): its own
/// DbContext, its own physical database, its own migration history. The monolith never sees it — the
/// two communicate only over the HTTP contract. One payment per order is enforced by a unique index
/// on <see cref="OrderId"/> (idempotency — a redelivered charge cannot double-charge).
/// </summary>
public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }

    /// <summary>Who placed the order this payment is for (ADR-031: resource ownership, defense in depth).
    /// Carried in from the <c>order-placed</c> Kafka event or the caller of <c>POST /payments/charge</c> —
    /// Payment does not have its own copy of the orders table, so this is the ONLY way it can tell "is the
    /// caller of GET /payments/{orderId} the person who placed it, or Admin, or neither". Null on a payment
    /// created before this field existed, or via a caller that genuinely doesn't know the customer (an
    /// admin/support-triggered charge) — GET treats a null CustomerId as "no non-admin may read this".</summary>
    public Guid? CustomerId { get; set; }

    public Money Amount { get; set; } = null!;
    public string Method { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public string? GatewayReference { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Opaque, one-way token (ADR-046) — never the raw card number. Null when the charge came
    /// through without card details (e.g. a saved payment method already tokenized upstream).</summary>
    public string? CardToken { get; set; }

    /// <summary>Last 4 digits only — already public on the physical card and every receipt.</summary>
    public string? CardLast4 { get; set; }
}

public enum PaymentStatus
{
    Pending,
    Completed,
    Failed,
    Refunded
}
