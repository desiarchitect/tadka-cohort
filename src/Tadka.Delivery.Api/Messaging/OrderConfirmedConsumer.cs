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
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.OrderConfirmed);
        logger.LogInformation("OrderConfirmedConsumer subscribed to {Topic}.", Topics.OrderConfirmed);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;
                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(   // rejoin the trace (ADR-041)
                    $"consume {Topics.OrderConfirmed}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));
                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "OrderConfirmedConsumer consume error."); }
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
            logger.LogWarning(ex, "order-confirmed at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "order-confirmed at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        await producer.PublishAsync(Topics.OrderConfirmedDlq, cr.Message.Key,
            new DlqMessage(Topics.OrderConfirmed, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);

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

        var result = await scope.ServiceProvider.GetRequiredService<DeliveryService>().AssignAsync(msg.OrderId, ct);
        if (result is not null)
            await producer.PublishAsync(Topics.DeliveryAssigned, msg.OrderId.ToString(),
                new DeliveryAssignedMessage(Guid.NewGuid(), msg.OrderId, result.AgentId, result.AgentName), ct);

        db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
    }
}
