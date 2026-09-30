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
    KafkaProducer producer, // only for dead-lettering a message that keeps failing (ADR-051)
    IOptions<KafkaOptions> options,
    ILogger<PaymentRefundedConsumer> logger) : BackgroundService
{
    private readonly PoisonMessageTracker _poison = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }.ApplySasl(options.Value);

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.PaymentRefunded);
        logger.LogInformation("PaymentRefundedConsumer subscribed to {Topic}.", Topics.PaymentRefunded);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr); // at-least-once: commit only after processing
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "PaymentRefundedConsumer consume error."); }
            catch (Exception ex) when (cr is not null)
            {
                await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
            }
        }

        consumer.Close();
    }, stoppingToken);

    // ADR-051: a handler failure used to be logged and skipped, but Consume() advances the read position on every
    // call regardless of commit, so committing the NEXT message silently committed past the failed one. Now:
    // seek back to retry the SAME message a bounded number of times, then quarantine it on payment-refunded.dlq and commit.
    private async Task HandlePoisonAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> cr, Exception ex, CancellationToken ct)
    {
        if (!_poison.RecordFailureAndShouldDlq(cr.TopicPartitionOffset))
        {
            logger.LogWarning(ex, "payment-refunded at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "payment-refunded at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.PaymentRefundedDlq, cr.Message.Key,
                new DlqMessage(Topics.PaymentRefunded, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            // Could not quarantine it (broker down). Do NOT commit past it and do NOT let this escape the loop
            // (an exception out of a catch block would stop the whole host): rewind and try again later.
            logger.LogError(dlqEx, "Could not publish payment-refunded at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

        consumer.Commit(cr); // now genuinely unblock: this offset is quarantined, not silently lost
        _poison.Clear(cr.TopicPartitionOffset);
    }

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
        await trackingBus.PublishAsync(
            new OrderTrackingEvent(msg.OrderId, "Cancelled", "Your refund has been processed.", DateTime.UtcNow), ct);

        db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Order {OrderId} refund settled ({Status}).", msg.OrderId, msg.Status);
    }
}
