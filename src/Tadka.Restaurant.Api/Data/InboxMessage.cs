namespace Tadka.Restaurant.Api.Data;

/// <summary>Inbox dedup for order-confirmed (ADR-028) on the Restaurant service.</summary>
public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ConsumedAt { get; set; } = DateTime.UtcNow;
}
