using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;

namespace Tadka.Payment.Api.Messaging;

/// <summary>
/// Compensating consumer (ADR-045): restaurant rejected an already-paid order → Ordering published
/// <c>refund-requested</c> via Outbox → we refund the charge and publish <c>payment-refunded</c>.
/// Same production shape as <see cref="OrderPlacedConsumer"/>: Inbox dedup, manual commit, idempotent domain.
/// </summary>
public sealed class RefundRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
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
        }.ApplySasl(options.Value);

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

        var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
        var outcome = await payments.RefundAsync(msg.OrderId, msg.GatewayReference, ct);

        await producer.PublishAsync(Topics.PaymentRefunded, msg.OrderId.ToString(),
            new PaymentRefundedMessage(Guid.NewGuid(), msg.OrderId, outcome.Status.ToString()), ct);

        db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
    }
}
