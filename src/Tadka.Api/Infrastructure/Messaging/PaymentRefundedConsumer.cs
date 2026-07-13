using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Infrastructure.Realtime;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// Ordering's reply-side of the refund saga (ADR-045): consumes <c>payment-refunded</c> and surfaces it
/// on the live-tracking stream (ADR-020). The order itself is already <c>Cancelled</c> by this point
/// (see <see cref="RefundSagaOrchestrator"/>) — this consumer exists purely to close the loop for the
/// customer: "your money is back," not just "your order was cancelled."
/// </summary>
public sealed class PaymentRefundedConsumer(
    IServiceScopeFactory scopeFactory,
    IOrderTrackingBus trackingBus,
    IOptions<KafkaOptions> options,
    ILogger<PaymentRefundedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.PaymentRefunded);
        logger.LogInformation("PaymentRefundedConsumer subscribed to {Topic}.", Topics.PaymentRefunded);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "PaymentRefundedConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "PaymentRefundedConsumer handler error."); }
        }

        consumer.Close();
    }, stoppingToken);

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<PaymentRefundedMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

        if (await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("payment-refunded {MessageId} already processed — skipping (idempotent).", msg.MessageId);
            return;
        }

        // Side effect first (SSE surface), then inbox — crash mid-way redelivers (publish is at-most-once ok).
        await trackingBus.PublishAsync(
            new OrderTrackingEvent(msg.OrderId, "Cancelled", "Your refund has been processed.", DateTime.UtcNow), ct);

        db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Order {OrderId} refund settled ({Status}).", msg.OrderId, msg.Status);
    }
}
