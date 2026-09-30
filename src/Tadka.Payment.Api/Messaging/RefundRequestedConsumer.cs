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
    private readonly PoisonMessageTracker _poison = new();

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
            catch (ConsumeException ex) { logger.LogError(ex, "RefundRequestedConsumer consume error."); }
            catch (Exception ex) when (cr is not null)
            {
                await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
            }
        }

        consumer.Close();
    }, stoppingToken);

    // ADR-051: a handler failure used to be logged and skipped, but Consume() advances the read position on every
    // call regardless of commit, so committing the NEXT message silently committed past the failed one. Now:
    // seek back to retry the SAME message a bounded number of times, then quarantine it on refund-requested.dlq and commit.
    private async Task HandlePoisonAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> cr, Exception ex, CancellationToken ct)
    {
        if (!_poison.RecordFailureAndShouldDlq(cr.TopicPartitionOffset))
        {
            logger.LogWarning(ex, "refund-requested at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "refund-requested at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.RefundRequestedDlq, cr.Message.Key,
                new DlqMessage(Topics.RefundRequested, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            // Could not quarantine it (broker down). Do NOT commit past it and do NOT let this escape the loop
            // (an exception out of a catch block would stop the whole host): rewind and try again later.
            logger.LogError(dlqEx, "Could not publish refund-requested at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

        consumer.Commit(cr); // now genuinely unblock: this offset is quarantined, not silently lost
        _poison.Clear(cr.TopicPartitionOffset);
    }

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
