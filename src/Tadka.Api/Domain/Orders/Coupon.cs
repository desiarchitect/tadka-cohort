namespace Tadka.Api.Domain.Orders;

/// <summary>
/// A limited-use discount coupon - the Day 4 high-contention demo (ADR-045).
/// A flash-sale-shaped write: many customers race to claim a scarce resource
/// (Tatkal tickets, BookMyShow seats, "first 100 orders get 50% off").
///
/// The interesting part is not the entity - it is which locking strategy guards
/// Redeemed. See CouponsController: None (lost updates -> oversell), Optimistic
/// (correct but a 409 retry storm under contention), Pessimistic (SELECT ... FOR
/// UPDATE serializes the hot row - the decided fix for this access pattern).
/// </summary>
public class Coupon
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public int MaxRedemptions { get; set; }
    public int Redeemed { get; set; }

    public int Remaining => MaxRedemptions - Redeemed;
    public bool IsExhausted => Redeemed >= MaxRedemptions;
}

/// <summary>
/// One successful redemption. The row count is the ground truth the break demo
/// checks: with no locking, COUNT(*) exceeds MaxRedemptions (oversell) while the
/// Redeemed counter under-counts (lost updates) - two symptoms, same race.
/// </summary>
public class CouponRedemption
{
    public Guid Id { get; set; }
    public Guid CouponId { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime RedeemedAt { get; set; }
}
