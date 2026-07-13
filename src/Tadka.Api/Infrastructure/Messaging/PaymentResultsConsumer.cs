using System.Text.Json;
using Confluent.Kafka;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Domain.Common.Events;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// The Saga reaction on the Ordering side (ADR-027/028/029): consumes <c>payment-results</c> from Kafka,
/// dedups via the Inbox (idempotent — at-least-once redelivery is skipped), then republishes the shared
/// <see cref="PaymentCompletedEvent"/>/<see cref="PaymentFailedEvent"/> through MediatR so the EXISTING
/// order-reaction handlers run (confirm / compensating-cancel). Offset is committed only AFTER processing.
/// </summary>
public sealed class PaymentResultsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<PaymentResultsConsumer> logger) : BackgroundService
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
        consumer.Subscribe(Topics.PaymentResults);
        logger.LogInformation("PaymentResultsConsumer subscribed to {Topic}.", Topics.PaymentResults);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr); // at-least-once: commit only after we've processed it
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "PaymentResultsConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "PaymentResultsConsumer handler error."); }
        }

        consumer.Close();
    }, stoppingToken);

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<PaymentResultMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

        // Inbox AFTER side effect (ADR-028 Day-9 invariant): check ? work ? stamp inbox ? commit offset.
        if (await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("payment-results {MessageId} already processed — skipping (idempotent).", msg.MessageId);
            return;
        }
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        if (string.Equals(msg.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            await mediator.Publish(new PaymentCompletedEvent(msg.OrderId, msg.GatewayReference ?? ""), ct);
        else
            await mediator.Publish(new PaymentFailedEvent(msg.OrderId, msg.FailureReason ?? "Payment failed"), ct);

        db.Set<InboxMessage>().Add(new InboxMessage { MessageId = msg.MessageId });
        await db.SaveChangesAsync(ct);
    }
}
