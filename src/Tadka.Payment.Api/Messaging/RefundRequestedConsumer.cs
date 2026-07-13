using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;
using Tadka.Telemetry;

namespace Tadka.Payment.Api.Messaging;

/// <summary>
/// Compensating consumer (ADR-045): restaurant rejected an already-paid order → Ordering published
/// <c>refund-requested</c> via Outbox → we refund the charge and Outbox <c>payment-refunded</c>.
/// Same production shape as <see cref="OrderPlacedConsumer"/>: Inbox + Outbox + manual commit.
/// </summary>
public sealed class RefundRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<RefundRequestedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroup + "-refunds",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.RefundRequested);
        logger.LogInformation("RefundRequestedConsumer subscribed to {Topic}.", Topics.RefundRequested);

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
            catch (ConsumeException ex) { logger.LogError(ex, "RefundRequestedConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "RefundRequestedConsumer handler error — will reprocess (idempotent)."); }
        }

        consumer.Close();
    }, stoppingToken);

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<RefundRequestedMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        if (await db.InboxMessages.AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("refund-requested {MessageId} already processed — skipping (idempotent).", msg.MessageId);
            return;
        }

        // Refund row + payment-refunded outbox + inbox in ONE transaction (same DbContext as PaymentService).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var outcome = await payments.RefundAsync(msg.OrderId, msg.GatewayReference, ct);

            var refunded = new PaymentRefundedMessage(Guid.NewGuid(), msg.OrderId, outcome.Status.ToString());
            db.OutboxMessages.Add(new OutboxMessage
            {
                Topic = Topics.PaymentRefunded,
                Key = msg.OrderId.ToString(),
                Payload = JsonSerializer.Serialize(refunded),
                TraceParent = TadkaTrace.CurrentTraceParent()
            });

            db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
