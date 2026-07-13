using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Domain.Restaurants;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// Application handler for Restaurant accept/reject (ADR-062). Invoked by the Kafka consumer and by
/// integration tests without a broker — keeps Service-mode compensation testable.
/// </summary>
public sealed class RestaurantResponseHandler(
    TadkaDbContext db,
    IOrderRepository orders,
    RefundSagaOrchestrator refundSaga,
    IOptions<RestaurantAcceptanceOptions> restaurantOptions,
    ILogger<RestaurantResponseHandler> logger)
{
    public async Task HandleAsync(RestaurantResponseMessage msg, CancellationToken ct = default)
    {
        // Inbox AFTER side effects, same DB transaction (ADR-028).
        if (await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("restaurant-response {MessageId} already processed — skip.", msg.MessageId);
            return;
        }

        if (!string.Equals(msg.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Restaurant accepted order {OrderId}.", msg.OrderId);
            db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
            await db.SaveChangesAsync(ct);
            return;
        }

        var order = await orders.GetByIdAsync(msg.OrderId);
        if (order is null)
        {
            logger.LogWarning("restaurant-response for unknown order {OrderId} — skip without inbox.", msg.OrderId);
            return;
        }

        // Compensation (cancel + refund-requested outbox) + inbox in ONE transaction so we never
        // cancel without stamping inbox (or stamp inbox without cancel) across a crash boundary.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await refundSaga.RejectAndCompensateAsync(
                order, msg.GatewayReference, restaurantOptions.Value.RefundOnReject, ct);

            if (!await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
            {
                db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
