using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Domain;
using Tadka.Telemetry;

namespace Tadka.Restaurant.Api.Messaging;

/// <summary>
/// Production path (ADR-062): consume <c>order-confirmed</c>, decide Accept/Reject via
/// <c>Restaurant:AcceptMode</c>, persist audit row, Outbox <c>restaurant-response</c>.
/// </summary>
public sealed class OrderConfirmedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    IOptionsMonitor<RestaurantOptions> restaurantOptions,
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

                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(
                    $"consume {Topics.OrderConfirmed}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "OrderConfirmedConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "OrderConfirmedConsumer handler error."); }
        }

        consumer.Close();
    }, stoppingToken);

    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<OrderConfirmedMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();

        if (await db.InboxMessages.AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("order-confirmed {MessageId} already decided — skip.", msg.MessageId);
            return;
        }

        if (await db.OrderDecisions.AnyAsync(d => d.OrderId == msg.OrderId, ct))
        {
            db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
            await db.SaveChangesAsync(ct);
            return;
        }

        var reject = string.Equals(restaurantOptions.CurrentValue.AcceptMode, "Reject", StringComparison.OrdinalIgnoreCase);
        var status = reject ? "Rejected" : "Accepted";
        var reason = reject ? "Restaurant:AcceptMode=Reject (demo)" : null;

        db.OrderDecisions.Add(new OrderDecision
        {
            OrderId = msg.OrderId,
            Status = status,
            Reason = reason,
            DecidedAt = DateTime.UtcNow
        });

        var response = new RestaurantResponseMessage(
            Guid.NewGuid(), msg.OrderId, status, reason, GatewayReference: null);

        db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = Topics.RestaurantResponse,
            Key = msg.OrderId.ToString(),
            Payload = JsonSerializer.Serialize(response),
            TraceParent = TadkaTrace.CurrentTraceParent()
        });

        db.InboxMessages.Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Restaurant {Status} order {OrderId}.", status, msg.OrderId);
    }
}
