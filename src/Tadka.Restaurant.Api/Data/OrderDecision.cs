namespace Tadka.Restaurant.Api.Data;

/// <summary>Audit + idempotency for restaurant accept/reject (ADR-062).</summary>
public sealed class OrderDecision
{
    public Guid OrderId { get; set; }
    public string Status { get; set; } = default!; // Accepted | Rejected
    public string? Reason { get; set; }
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}
