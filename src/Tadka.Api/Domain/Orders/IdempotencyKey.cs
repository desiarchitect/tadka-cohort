namespace Tadka.Api.Domain.Orders;

/// <summary>
/// Records that a client-supplied Idempotency-Key has already produced an order.
/// A replayed key returns the original order instead of creating a duplicate — the fix for the
/// "double-tap Place Order" break. Written in the SAME transaction as the order, so the key and
/// the order it created commit together or not at all. See ADR-011.
/// </summary>
public class IdempotencyKey
{
    public string Key { get; set; } = null!;   // the client-supplied Idempotency-Key header
    public Guid OrderId { get; set; }           // the order this key created
    public DateTime CreatedAt { get; set; }
}
