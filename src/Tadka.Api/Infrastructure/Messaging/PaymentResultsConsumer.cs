using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Domain.Common.Events;
using Tadka.Telemetry;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// The Saga reaction on the Ordering side (ADR-027/028/029): consumes <c>payment-results</c> from Kafka,
/// dedups via the Inbox (idempotent — at-least-once redelivery is skipped), then republishes the shared
/// <see cref="PaymentCompletedEvent"/>/<see cref="PaymentFailedEvent"/> through MediatR so the EXISTING
/// order-reaction handlers run (confirm / compensating-cancel). Offset is committed only AFTER processing.
/// </summary>
public sealed class PaymentResultsConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    IOptions<KafkaOptions> options,
    ILogger<PaymentResultsConsumer> logger) : BackgroundService
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
        consumer.Subscribe(Topics.PaymentResults);
        logger.LogInformation("PaymentResultsConsumer subscribed to {Topic}.", Topics.PaymentResults);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                // Rejoin the order's trace: read the traceparent the producer injected and open a consume
                // span as a remote child (ADR-041). Missing header ⇒ a new root (graceful).
                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(
                    $"consume {Topics.PaymentResults}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr); // at-least-once: commit only after we've processed it
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "PaymentResultsConsumer consume error."); }
            catch (Exception ex) when (cr is not null)
            {
                await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
            }
        }

        consumer.Close();
    }, stoppingToken);

    // Pull the W3C traceparent the producer stamped on the message headers (ADR-041).
    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private async Task HandlePoisonAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> cr, Exception ex, CancellationToken ct)
    {
        if (!_poison.RecordFailureAndShouldDlq(cr.TopicPartitionOffset))
        {
            logger.LogWarning(ex, "payment-results at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "payment-results at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.PaymentResultsDlq, cr.Message.Key,
                new DlqMessage(Topics.PaymentResults, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            // Could not quarantine it (broker down). Do NOT commit past it and do NOT let this escape the loop
            // (an exception out of a catch block would stop the whole host): rewind and try again later.
            logger.LogError(dlqEx, "Could not publish payment-results at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

        consumer.Commit(cr); // now genuinely unblock: this offset is quarantined, not silently lost
        _poison.Clear(cr.TopicPartitionOffset);
    }

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<PaymentResultMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

        // Inbox dedup (ADR-028): skip only if already fully processed.
        if (await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("payment-results {MessageId} already processed — skipping (idempotent).", msg.MessageId);
            return;
        }

        // ONE transaction: domain reaction (confirm/cancel + outbox) + inbox stamp.
        // Handlers call SaveChanges on this same scoped DbContext; EF enrolls those flushes in the tx
        // until Commit — so we never "confirm committed, inbox not yet" (or the reverse).
        // ADR-064: the unit runs inside the execution strategy so a retrying strategy can replay it from the
        // top after a transient fault (flag off = runs once, exactly as before).
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // a replay starts clean: no half-applied order/inbox from the failed attempt
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                if (string.Equals(msg.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                    await mediator.Publish(new PaymentCompletedEvent(msg.OrderId, msg.GatewayReference ?? ""), ct);
                else
                    await mediator.Publish(new PaymentFailedEvent(msg.OrderId, msg.FailureReason ?? "Payment failed"), ct);

                db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw; // no Kafka commit → redelivery; handlers are idempotent
            }
        });
    }
}
