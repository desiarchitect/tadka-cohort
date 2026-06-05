using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;

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
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;
                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "OrderConfirmedConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "OrderConfirmedConsumer handler error — will reprocess (idempotent)."); }
        }

        consumer.Close();
    }, stoppingToken);

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
