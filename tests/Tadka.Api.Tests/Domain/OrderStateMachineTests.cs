using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Tests.Domain;

// Pure unit tests for the order state machine — the domain rule that 422s are built on.
// No database, no Docker: the aggregate enforces transitions in memory.
public class OrderStateMachineTests
{
    [Fact]
    public void Transition_Created_to_Confirmed_succeeds_and_stamps_ConfirmedAt()
    {
        var order = new Order { Status = OrderStatus.Created };

        var result = order.Transition(OrderStatus.Confirmed);

        Assert.False(result.IsFailure);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.NotNull(order.ConfirmedAt);
    }

    [Fact]
    public void Transition_Created_to_Preparing_is_illegal_and_state_unchanged()
    {
        var order = new Order { Status = OrderStatus.Created };

        var result = order.Transition(OrderStatus.Preparing);

        Assert.True(result.IsFailure);
        Assert.Equal(OrderStatus.Created, order.Status); // not mutated on failure
    }

    [Fact]
    public void Delivered_is_terminal_no_transition_allowed()
    {
        var order = new Order { Status = OrderStatus.Delivered };

        Assert.True(order.Transition(OrderStatus.Refunded).IsFailure);  // Refunded is Day-7, not wired
        Assert.True(order.Transition(OrderStatus.Confirmed).IsFailure);
        Assert.Equal(OrderStatus.Delivered, order.Status);
    }

    [Fact]
    public void Cancel_from_Created_succeeds_with_reason()
    {
        var order = new Order { Status = OrderStatus.Created };

        var result = order.Cancel("changed my mind");

        Assert.False(result.IsFailure);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("changed my mind", order.CancellationReason);
        Assert.NotNull(order.CancelledAt);
    }

    [Fact]
    public void Cancel_after_PickedUp_is_illegal()
    {
        var order = new Order { Status = OrderStatus.PickedUp };

        Assert.True(order.Cancel("too late").IsFailure);
        Assert.Equal(OrderStatus.PickedUp, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Created, OrderStatus.Confirmed, true)]
    [InlineData(OrderStatus.Created, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Preparing, true)]
    [InlineData(OrderStatus.Preparing, OrderStatus.ReadyForPickup, true)]
    [InlineData(OrderStatus.ReadyForPickup, OrderStatus.PickedUp, true)]
    [InlineData(OrderStatus.PickedUp, OrderStatus.Delivered, true)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Delivered, false)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled, false)]
    public void CanTransition_matches_the_state_machine(OrderStatus from, OrderStatus to, bool expected)
    {
        Assert.Equal(expected, OrderStateMachine.CanTransition(from, to));
    }
}
