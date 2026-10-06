using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;
using Tadka.Telemetry;

namespace Tadka.Delivery.Api.Messaging;

/// <summary>
/// The Delivery service's place in the Saga (ADR-029/033): consumes <c>order-confirmed</c>, assigns a
/// rider (idempotent), and publishes <c>delivery-assigned</c>. At-least-once + Inbox dedup + the
/// one-assignment-per-order unique index → a redelivery never double-assigns. A down service just resumes
/// from its offset (messages wait in Kafka).
/// </summary>
public sealed class OrderConfirmedConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    IOptions<KafkaOptions> options,
    ILogger<OrderConfirmedConsumer> logger) : BackgroundService
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
        consumer.Subscribe(Topics.OrderConfirmed);
        logger.LogInformation("OrderConfirmedConsumer subscribed to {Topic}.", Topics.OrderConfirmed);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            Activity? activity = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;
                activity = TadkaDiagnostics.ActivitySource.StartActivity(   // rejoin the trace (ADR-041)
                    $"consume {Topics.OrderConfirmed}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));
                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                logger.LogError(ex, "OrderConfirmedConsumer consume error.");
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
            logger.LogWarning(ex, "order-confirmed at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "order-confirmed at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.OrderConfirmedDlq, cr.Message.Key,
                new DlqMessage(Topics.OrderConfirmed, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            // Could not quarantine it (broker down). Do NOT commit past it and do NOT let this escape the loop
            // (an exception out of a catch block would stop the whole host): rewind and try again later.
            logger.LogError(dlqEx, "Could not publish order-confirmed at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

        consumer.Commit(cr);
        _poison.Clear(cr.TopicPartitionOffset);
    }

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<OrderConfirmedMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();

        if (await db.InboxMessages.AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("order-confirmed {MessageId} already processed — skipping (idempotent).", msg.MessageId);
            return;
        }

        // No rider free: AssignAsync parks the order in pending_assignments (durable) and returns null; the
        // PendingAssignmentSweeper assigns it later. So stamping the Inbox below no longer drops the order.
        var result = await scope.ServiceProvider.GetRequiredService<DeliveryService>()
            .AssignAsync(msg.OrderId, ct, msg.CustomerId, msg.Latitude, msg.Longitude);
        if (result is not null)
            await producer.PublishAsync(Topics.DeliveryAssigned, msg.OrderId.ToString(),
                new DeliveryAssignedMessage(Guid.NewGuid(), msg.OrderId, result.AgentId, result.AgentName), ct);

        db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
    }
}
