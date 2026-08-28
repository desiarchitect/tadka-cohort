namespace Tadka.Api.Domain.Orders;

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
