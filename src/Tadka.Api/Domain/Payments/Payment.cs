using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Payments;

public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Money Amount { get; set; } = null!;
    public string Method { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public string? GatewayReference { get; set; }
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
