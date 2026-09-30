using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Domain.Common.Events;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Infrastructure.Messaging;

namespace Tadka.Api.Domain.Orders.Events.Handlers;

/// <summary>
/// Ordering's reaction to a settled payment (ADR-022/023). These handlers depend on the SHARED event
/// contract (<see cref="PaymentCompletedEvent"/>/<see cref="PaymentFailedEvent"/>) — never on the Payment
/// module. Payment publishes; Ordering reacts; neither references the other. The resulting order status
/// change is re-published so it rides the Day-6 SSE stream (ADR-020) to the customer's screen — which is
/// what makes async payment feel instant: "Created" → (moments later) "Confirmed".
///
/// Day 11 (ADR-045): before auto-confirming, the (in-process, until Day 12) restaurant gets a say. If it
/// rejects a payment that already completed, this is no longer a simple confirm — it's a compensating
/// action (Saga refund), delegated to <see cref="RefundSagaOrchestrator"/>.
/// </summary>
public sealed class ConfirmOrderOnPaymentCompleted(
    IOrderRepository orders,
    TadkaDbContext db,
    IMediator mediator,
    IOptions<RestaurantAcceptanceOptions> restaurantOptions,
    RefundSagaOrchestrator refundSaga,
    ILogger<ConfirmOrderOnPaymentCompleted> logger) : INotificationHandler<PaymentCompletedEvent>
{
    public async Task Handle(PaymentCompletedEvent notification, CancellationToken cancellationToken)
    {
        var order = await orders.GetByIdAsync(notification.OrderId);
        if (order is null) return;

        if (order.Status != OrderStatus.Created)
        {
            // Order already moved on (manually confirmed/cancelled). Payment is recorded; nothing to do.
            logger.LogInformation("Payment completed for order {OrderId}, but it is '{Status}' — no auto-confirm.",
                notification.OrderId, order.Status);
            return;
        }

        if (string.Equals(restaurantOptions.Value.AcceptMode, "Reject", StringComparison.OrdinalIgnoreCase))
        {
            await refundSaga.RejectAndCompensateAsync(order, notification.GatewayReference, restaurantOptions.Value.RefundOnReject, cancellationToken);
            return;
        }

        var result = order.Transition(OrderStatus.Confirmed);
        if (result.IsFailure)
        {
            logger.LogInformation("Payment completed for order {OrderId}, but it is '{Status}' — no auto-confirm.",
                notification.OrderId, order.Status);
            return;
        }

        // Saga continues to the 3rd participant (ADR-029/033): publish `order-confirmed` via the Outbox, in
        // the SAME transaction as the confirm — the Delivery service consumes it and assigns a rider. The
        // event carries the delivery lat/long so Delivery needs no back-call (ADR-008).
        var confirmed = new OrderConfirmedMessage(Guid.NewGuid(), order.Id, order.DeliveryAddress.Latitude, order.DeliveryAddress.Longitude, order.CustomerId);
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Topic = Topics.OrderConfirmed,
            Key = order.Id.ToString(),
            Payload = JsonSerializer.Serialize(confirmed)
        });

        await orders.SaveChangesAsync();
        await PublishAndClear(order, mediator);
        logger.LogInformation("Order {OrderId} auto-confirmed after successful payment.", notification.OrderId);
    }

    internal static async Task PublishAndClear(Order order, IMediator mediator)
    {
        // Snapshot-then-clear before publishing (see OrdersController.PublishEventsAsync) — avoids
        // mutating a live event list if publishing re-enters this aggregate.
        var events = order.DomainEvents.ToList();
        order.ClearDomainEvents();
        foreach (var domainEvent in events)
            await mediator.Publish(domainEvent);
    }
}

/// <summary>A failed/timed-out payment cancels the order (ADR-023) — through the same state machine.</summary>
public sealed class CancelOrderOnPaymentFailed(
    IOrderRepository orders,
    IMediator mediator,
    ILogger<CancelOrderOnPaymentFailed> logger) : INotificationHandler<PaymentFailedEvent>
{
    public async Task Handle(PaymentFailedEvent notification, CancellationToken cancellationToken)
    {
        var order = await orders.GetByIdAsync(notification.OrderId);
        if (order is null) return;

        var result = order.Cancel($"Payment failed: {notification.Reason}");
        if (result.IsFailure)
        {
            logger.LogWarning("Payment failed for order {OrderId}, but it could not be cancelled (status '{Status}').",
                notification.OrderId, order.Status);
            return;
        }

        await orders.SaveChangesAsync();
        await ConfirmOrderOnPaymentCompleted.PublishAndClear(order, mediator);
        logger.LogWarning("Order {OrderId} cancelled after payment failure.", notification.OrderId);
    }
}
