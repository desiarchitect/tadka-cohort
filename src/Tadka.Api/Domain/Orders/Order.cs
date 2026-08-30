using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Domain.Common;

namespace Tadka.Api.Domain.Orders;

/// <summary>
/// The Order aggregate root. State is encapsulated: every property is read-only from the outside,
/// and the ONLY way to change status is <see cref="Transition"/> / <see cref="Cancel"/>, which run
/// the state machine first. There is deliberately no public setter for Status — a caller cannot do
/// <c>order.Status = Delivered</c> and skip the rules. (This is the "aggregate root is a gatekeeper,
/// not just a container" point taught on Day 2.)
/// </summary>
public class Order
{
    private readonly List<OrderItem> _items = [];

    // Parameterless ctor for EF Core materialization only.
    private Order() { }

    /// <summary>Creates a new order. An order is always born <see cref="OrderStatus.Created"/>.</summary>
    public Order(Guid customerId, Guid restaurantId, List<OrderItem> items, Money totalAmount, Address deliveryAddress)
    {
        Id = Guid.NewGuid();
        CustomerId = customerId;
        RestaurantId = restaurantId;
        Status = OrderStatus.Created;
        _items.AddRange(items);
        TotalAmount = totalAmount;
        DeliveryAddress = deliveryAddress;
        CreatedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid RestaurantId { get; private set; }
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<OrderItem> Items => _items;
    public Money TotalAmount { get; private set; } = null!;
    public Address DeliveryAddress { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    // Test-only seam: build an order already in a given state so a unit test can exercise a
    // transition from it. internal + [InternalsVisibleTo] means only the test assembly can reach it.
    internal static Order InState(OrderStatus status) => new() { Status = status };

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
