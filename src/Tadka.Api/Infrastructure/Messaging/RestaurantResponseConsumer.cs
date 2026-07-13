using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Domain.Restaurants;
using Tadka.Telemetry;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// Production multi-service path (ADR-062): Restaurant.Api publishes <c>restaurant-response</c>.
/// On Rejected → compensate (cancel + optional refund). On Accepted → no-op (order already Confirmed).
/// </summary>
public sealed class RestaurantResponseConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    IOptions<RestaurantAcceptanceOptions> restaurantOptions,
    ILogger<RestaurantResponseConsumer> logger) : BackgroundService
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
        consumer.Subscribe(Topics.RestaurantResponse);
        logger.LogInformation("RestaurantResponseConsumer subscribed to {Topic}.", Topics.RestaurantResponse);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(
                    $"consume {Topics.RestaurantResponse}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "RestaurantResponseConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "RestaurantResponseConsumer handler error."); }
        }

        consumer.Close();
    }, stoppingToken);

    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<RestaurantResponseMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

        // Inbox AFTER side effects (same discipline as PaymentResultsConsumer / Payment OrderPlacedConsumer).
        if (await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("restaurant-response {MessageId} already processed — skip.", msg.MessageId);
            return;
        }

        if (!string.Equals(msg.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Restaurant accepted order {OrderId}.", msg.OrderId);
            db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
            await db.SaveChangesAsync(ct);
            return;
        }

        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var order = await orders.GetByIdAsync(msg.OrderId);
        if (order is null)
        {
            // Don't inbox-stamp unknowns — redelivery may race creation; commit offset only after effect.
            logger.LogWarning("restaurant-response for unknown order {OrderId} — skip without inbox.", msg.OrderId);
            return;
        }

        var refundSaga = scope.ServiceProvider.GetRequiredService<RefundSagaOrchestrator>();
        await refundSaga.RejectAndCompensateAsync(
            order,
            msg.GatewayReference,
            restaurantOptions.Value.RefundOnReject,
            ct);

        // Saga SaveChanges already ran; stamp inbox on a fresh write (same DbContext instance after save).
        if (!await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
            await db.SaveChangesAsync(ct);
        }
    }
}
