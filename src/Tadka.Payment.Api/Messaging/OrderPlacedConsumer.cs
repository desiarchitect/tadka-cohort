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
    IOptions<PaymentOptions> paymentOptions,
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
        }.ApplySasl(options.Value);

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.OrderPlaced);
        logger.LogInformation("OrderPlacedConsumer subscribed to {Topic}.", Topics.OrderPlaced);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            Activity? activity = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                // Open a consume span under the order's trace (ADR-041); the charge + payment-results
                // publish below then hang off this span — so the whole saga is one Jaeger waterfall.
                activity = TadkaDiagnostics.ActivitySource.StartActivity(
                    $"consume {Topics.OrderPlaced}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr); // at-least-once: commit only after the charge + result are done
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                logger.LogError(ex, "OrderPlacedConsumer consume error.");
            }
            catch (GatewayUnavailableRetryLaterException ex) when (cr is not null)
            {
                // Fix 2 / ADR-043 Buffer mode: the gateway was unreachable, not a business decline. This
                // is deliberately NOT routed through HandlePoisonAsync/PoisonMessageTracker — a breaker
                // rejection is not a poison message, so it must never count toward the DLQ attempt
                // budget, and its backoff is the breaker's own break duration, not the 300ms poison
                // retry delay (hammering an already-open circuit every 300ms helps nobody).
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                var breakSeconds = paymentOptions.Value.CircuitBreakSeconds <= 0 ? 60 : paymentOptions.Value.CircuitBreakSeconds;
                logger.LogWarning(ex, "order-placed at {Offset} buffered — gateway unavailable, seeking back and pausing ~{Seconds}s.",
                    cr.TopicPartitionOffset, breakSeconds);
                consumer.Seek(cr.TopicPartitionOffset);
                try { await Task.Delay(TimeSpan.FromSeconds(breakSeconds), stoppingToken); } catch (OperationCanceledException) { }
            }
            catch (Exception ex) when (cr is not null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
            }
            finally
            {
                activity?.Dispose();
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
            logger.LogWarning(ex, "order-placed at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            // Consume() advances the fetch position on every call regardless of commit, so without this
            // explicit seek the loop would simply move on to the NEXT message and silently drop this one
            // the moment that next message's offset gets committed. Seek forces real redelivery.
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "order-placed at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.OrderPlacedDlq, cr.Message.Key,
                new DlqMessage(Topics.OrderPlaced, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            // Could not quarantine it (broker down). Do NOT commit past it and do NOT let this escape the loop
            // (an exception out of a catch block would stop the whole host): rewind and try again later.
            logger.LogError(dlqEx, "Could not publish order-placed at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

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

        // Charge + payment-results outbox + inbox in ONE transaction (ADR-028).
        // ChargeAsync SaveChanges flushes into this transaction until Commit.
        // ADR-064: runs inside the execution strategy so a retrying strategy can replay the unit (flag off =
        // runs once). Trade-off named in ADR-064: the Pending payment row rolls back with the failed attempt,
        // so a replay calls the gateway AGAIN. Safe with the fake gateway; a real PSP needs an idempotency key.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // a replay starts clean: no Added rows left over from the failed attempt
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
                var outcome = await payments.ChargeAsync(msg.OrderId, new Money(msg.Amount, msg.Currency), ct, customerId: msg.CustomerId);

                var result = new PaymentResultMessage(
                    Guid.NewGuid(), msg.OrderId, outcome.Status.ToString(), outcome.GatewayReference, outcome.FailureReason);
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Topic = Topics.PaymentResults,
                    Key = msg.OrderId.ToString(),
                    Payload = JsonSerializer.Serialize(result),
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
        });
    }
}
