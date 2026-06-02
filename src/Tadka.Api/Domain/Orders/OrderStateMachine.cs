namespace Tadka.Api.Domain.Orders;

public static class OrderStateMachine
{
    private static readonly Dictionary<OrderStatus, HashSet<OrderStatus>> Transitions = new()
    {
        [OrderStatus.Created] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
        [OrderStatus.Confirmed] = [OrderStatus.Preparing, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.ReadyForPickup],
        [OrderStatus.ReadyForPickup] = [OrderStatus.PickedUp],
        [OrderStatus.PickedUp] = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [], // terminal in v1 — Refunded is a Day-7 payment concern, not wired yet
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Refunded] = []
    };

    public static bool CanTransition(OrderStatus current, OrderStatus next)
    {
        if (Transitions.TryGetValue(current, out var allowed))
        {
            return allowed.Contains(next);
        }
        return false;
    }

    public static IReadOnlyCollection<OrderStatus> GetAllowedTransitions(OrderStatus current)
    {
        if (Transitions.TryGetValue(current, out var allowed))
        {
            return allowed;
        }
        return Array.Empty<OrderStatus>();
    }
}
