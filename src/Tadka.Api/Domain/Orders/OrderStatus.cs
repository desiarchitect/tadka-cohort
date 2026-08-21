namespace Tadka.Api.Domain.Orders;

// The order lifecycle. The state machine that enforces legal transitions
// arrives on Day 4 — Day 2 only models the shape.
public enum OrderStatus
{
    Created,
    Confirmed,
    Preparing,
    ReadyForPickup,
    PickedUp,
    Delivered,
    Cancelled,
    Refunded
}
