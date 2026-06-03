using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Payments;

/// <summary>
/// A payment attempt for an order. Lives in the Payment module (ADR-022): its own DbContext, its own
/// schema, its own migration history. Ordering never touches this type — the two modules talk only
/// through events. One payment per order is enforced by a unique index on <see cref="OrderId"/>
/// (ADR-023: the background processor is idempotent — a redelivery cannot double-charge).
/// </summary>
public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Money Amount { get; set; } = null!;
    public string Method { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public string? GatewayReference { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum PaymentStatus
{
    Pending,
    Completed,
    Failed,
    Refunded
}
