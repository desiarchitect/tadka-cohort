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
