using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// The Outbox relay (ADR-028): polls unsent <see cref="OutboxMessage"/> rows, publishes them to Kafka,
/// and stamps them processed. At-least-once — if it crashes after publishing but before stamping, it
/// republishes; the consumer's Inbox dedups. This is the durable replacement for the Day-7/8 in-memory
/// queue: an event committed with the order can never be lost. (Production: MassTransit / Debezium CDC.)
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxRelay started — draining ordering.outbox_messages → Kafka.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

                var batch = await db.Set<OutboxMessage>()
                    .Where(m => m.ProcessedAt == null)
                    .OrderBy(m => m.CreatedAt)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var message in batch)
                {
                    await producer.PublishRawAsync(message.Topic, message.Key, message.Payload, stoppingToken);
                    message.ProcessedAt = DateTime.UtcNow;
                }

                if (batch.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("OutboxRelay published {Count} message(s).", batch.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OutboxRelay loop error — will retry.");
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
