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
/// HOW the sequencing is expressed and observed. Choreography: implicit in handlers. Orchestration:
/// named steps + a row in <c>ordering.saga_instances</c> for ops/queryability.
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
        SagaInstance? saga = null;
        if (orchestrated)
        {
            saga = new SagaInstance
            {
                OrderId = order.Id,
                SagaType = "refund-compensation",
                CurrentStep = "cancel-order",
                Status = "Running",
                Detail = "Restaurant rejected; starting compensation"
            };
            db.SagaInstances.Add(saga);
            logger.LogInformation("ORCHESTRATOR refund-saga[{OrderId}] step 1/2: cancel the order.", order.Id);
        }

        var cancelResult = order.Cancel("Restaurant rejected the order.");
        if (cancelResult.IsFailure)
        {
            if (saga is not null)
            {
                saga.Status = "Failed";
                saga.Detail = $"Could not cancel (status '{order.Status}')";
                saga.CompletedAt = DateTime.UtcNow;
                saga.UpdatedAt = DateTime.UtcNow;
            }
            logger.LogWarning("Restaurant rejected order {OrderId}, but it could not be cancelled (status '{Status}').",
                order.Id, order.Status);
            await orders.SaveChangesAsync();
            return;
        }

        if (refundOnReject)
        {
            if (saga is not null)
            {
                saga.CurrentStep = "request-refund";
                saga.UpdatedAt = DateTime.UtcNow;
            }
            if (orchestrated)
                logger.LogInformation("ORCHESTRATOR refund-saga[{OrderId}] step 2/2: request the compensating refund.", order.Id);

            var refundMessage = new RefundRequestedMessage(Guid.NewGuid(), order.Id, gatewayReference);
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Topic = Topics.RefundRequested,
                Key = order.Id.ToString(),
                Payload = JsonSerializer.Serialize(refundMessage),
                TraceParent = Tadka.Telemetry.TadkaTrace.CurrentTraceParent() // keep compensation on the order's trace (ADR-041)
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
            if (saga is not null)
            {
                saga.CurrentStep = "skip-refund";
                saga.Detail = "RefundOnReject=false — money stuck (demo break)";
            }
        }

        if (saga is not null)
        {
            saga.Status = "Completed";
            saga.CurrentStep = refundOnReject ? "refund-requested" : "cancelled-no-refund";
            saga.CompletedAt = DateTime.UtcNow;
            saga.UpdatedAt = DateTime.UtcNow;
        }

        await orders.SaveChangesAsync();
        await ConfirmOrderOnPaymentCompleted.PublishAndClear(order, mediator);

        if (orchestrated)
            logger.LogInformation("ORCHESTRATOR refund-saga[{OrderId}] complete.", order.Id);
    }
}
