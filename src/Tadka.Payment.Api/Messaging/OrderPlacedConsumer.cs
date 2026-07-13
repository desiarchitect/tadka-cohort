using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;

namespace Tadka.Payment.Api.Messaging;

/// <summary>
/// Consumes <c>order-placed</c> from Kafka (ADR-027), charges the order, and publishes <c>payment-results</c>
/// back (the Saga reply, ADR-029). At-least-once: the offset is committed only after processing; a
/// redelivery is safe because the charge is idempotent (one-payment-per-order unique index) and the Inbox
/// (ADR-028) records processed message-ids. A down/restarting consumer simply resumes from its offset —
/// messages WAIT in the topic, they are never lost (the Day-8 wound, healed).
///
/// A message that fails processing (malformed JSON, a breaking schema change) is a genuine correctness trap
/// with a plain manual-commit loop: Kafka's committed offset is a single monotonic watermark, not a sparse
/// per-message ack, so the moment ANY later message on this partition is processed and committed, an
/// uncommitted earlier failure is silently skipped forever — the order it belongs to is never charged and
/// nothing ever surfaces the loss. This consumer instead explicitly <see cref="IConsumer{TKey,TValue}.Seek"/>s
/// back to a failed offset to force real redelivery, up to a bounded number of attempts, then routes the
/// message to <see cref="Topics.OrderPlacedDlq"/> and commits past it (ADR-051) — quarantined, not lost.
/// </summary>
public sealed class OrderPlacedConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    IOptions<KafkaOptions> options,
    ILogger<OrderPlacedConsumer> logger) : BackgroundService
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
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.OrderPlaced);
        logger.LogInformation("OrderPlacedConsumer subscribed to {Topic}.", Topics.OrderPlaced);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr); // at-least-once: commit only after the charge + result are done
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "OrderPlacedConsumer consume error."); }
            catch (Exception ex) when (cr is not null)
            {
                await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
            }
        }

        consumer.Close();
    }, stoppingToken);

    private async Task HandlePoisonAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> cr, Exception ex, CancellationToken ct)
    {
        if (!_poison.RecordFailureAndShouldDlq(cr.TopicPartitionOffset))
        {
            logger.LogWarning(ex, "order-placed at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            // Consume() advances the fetch position on every call regardless of commit, so without this
            // explicit seek the loop would simply move on to the NEXT message and silently drop this one
            // the moment that next message's offset gets committed. Seek forces real redelivery.
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "order-placed at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        await producer.PublishAsync(Topics.OrderPlacedDlq, cr.Message.Key,
            new DlqMessage(Topics.OrderPlaced, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);

        consumer.Commit(cr); // now genuinely unblock: this offset is quarantined, not silently lost
        _poison.Clear(cr.TopicPartitionOffset);
    }

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<OrderPlacedMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        // Inbox dedup (ADR-028): already processed this message-id? Skip.
        if (await db.InboxMessages.AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("order-placed {MessageId} already processed — skipping (idempotent).", msg.MessageId);
            return;
        }

        // Charge (idempotent on order_id — a redelivery returns the existing outcome, never double-charges).
        var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
        var outcome = await payments.ChargeAsync(msg.OrderId, new Money(msg.Amount, msg.Currency), ct);

        // Reply on payment-results (the Saga), then record the inbox row, then the loop commits the offset.
        await producer.PublishAsync(Topics.PaymentResults, msg.OrderId.ToString(),
            new PaymentResultMessage(Guid.NewGuid(), msg.OrderId, outcome.Status.ToString(), outcome.GatewayReference, outcome.FailureReason), ct);

        db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
    }
}
