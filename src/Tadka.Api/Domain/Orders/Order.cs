using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders;

public class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid RestaurantId { get; set; }
    public OrderStatus Status { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public Money TotalAmount { get; set; } = null!;
    public Address DeliveryAddress { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    
    // DDD: Encapsulate state transitions in the aggregate root
    public Result Transition(OrderStatus nextStatus)
    {
        if (!OrderStateMachine.CanTransition(Status, nextStatus))
        {
            var allowed = OrderStateMachine.GetAllowedTransitions(Status);
            return Result.Failure($"Cannot transition from '{Status}' to '{nextStatus}'. Allowed transitions: {string.Join(", ", allowed)}");
        }

        Status = nextStatus;

        if (nextStatus == OrderStatus.Confirmed)
            ConfirmedAt = DateTime.UtcNow;
        else if (nextStatus == OrderStatus.Delivered)
            DeliveredAt = DateTime.UtcNow;

        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        var result = Transition(OrderStatus.Cancelled);
        if (result.IsFailure)
            return result;

        CancelledAt = DateTime.UtcNow;
        CancellationReason = reason;
        return Result.Success();
    }
}
