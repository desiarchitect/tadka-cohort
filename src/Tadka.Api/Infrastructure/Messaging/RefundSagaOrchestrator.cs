using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Orders.Events.Handlers;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// The refund-compensation saga's steps (ADR-045/046): cancel the order, then (if the demo lever
/// allows it) request a refund for the already-completed payment. Same DB writes and events in EITHER
/// <see cref="SagaOptions.Mode"/> — the difference between choreography and orchestration here is
/// entirely in HOW the sequencing is expressed and observed, not in the outcome. In choreography, this
/// exact sequence would instead live inline inside the event handler that reacts to the rejection, with
/// no single place naming the steps; in orchestration, it is named and logged explicitly, one place, so
/// an operator (or a trace, from Day 13 onward) can see the whole compensation as one unit.
/// </summary>
public sealed class RefundSagaOrchestrator(
    IOrderRepository orders,
    TadkaDbContext db,
    IMediator mediator,
    IOptions<SagaOptions> sagaOptions,
    ILogger<RefundSagaOrchestrator> logger)
{
    public async Task RejectAndCompensateAsync(Order order, string? gatewayReference, bool refundOnReject, CancellationToken ct)
    {
        var orchestrated = sagaOptions.Value.IsOrchestration;
        if (orchestrated)
            logger.LogInformation("ORCHESTRATOR refund-saga[{OrderId}] step 1/2: cancel the order.", order.Id);

        var cancelResult = order.Cancel("Restaurant rejected the order.");
        if (cancelResult.IsFailure)
        {
            logger.LogWarning("Restaurant rejected order {OrderId}, but it could not be cancelled (status '{Status}').",
                order.Id, order.Status);
            return;
        }

        if (refundOnReject)
        {
            if (orchestrated)
                logger.LogInformation("ORCHESTRATOR refund-saga[{OrderId}] step 2/2: request the compensating refund.", order.Id);

            var refundMessage = new RefundRequestedMessage(Guid.NewGuid(), order.Id, gatewayReference);
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Topic = Topics.RefundRequested,
                Key = order.Id.ToString(),
                Payload = JsonSerializer.Serialize(refundMessage)
            });
        }
        else
        {
            // DEMO LEVER OFF (Restaurant:RefundOnReject=false): the order cancels, but the ALREADY-
            // COMPLETED payment is never told. This is the break: money genuinely stuck, forever, until
            // the lever is flipped back and this order is manually reconciled.
            logger.LogWarning(
                "Order {OrderId} cancelled after restaurant rejection, but Restaurant:RefundOnReject is OFF — " +
                "the completed payment is NOT refunded. Money is stuck (this is the compensation-disabled demo).",
                order.Id);
        }

        await orders.SaveChangesAsync();
        await ConfirmOrderOnPaymentCompleted.PublishAndClear(order, mediator);

        if (orchestrated)
            logger.LogInformation("ORCHESTRATOR refund-saga[{OrderId}] complete.", order.Id);
    }
}
