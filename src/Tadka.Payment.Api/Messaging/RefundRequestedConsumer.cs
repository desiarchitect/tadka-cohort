using System.Diagnostics;
using System.Text;
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
        };

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

                // Rejoin the order's trace (ADR-041): without this, the compensation/refund half
                // of the saga is an orphan span — the outgoing payment-refunded outbox row below
                // would capture a null TraceParent.
                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(
                    $"consume {Topics.RefundRequested}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
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

    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

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
        await producer.PublishAsync(Topics.RefundRequestedDlq, cr.Message.Key,
            new DlqMessage(Topics.RefundRequested, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);

        consumer.Commit(cr);
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
