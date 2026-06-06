using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Domain;
using Tadka.Telemetry;

namespace Tadka.Restaurant.Api.Messaging;

/// <summary>
/// The Outbox relay (ADR-028), same shape as the monolith's: claims unsent <see cref="OutboxMessage"/>
/// rows with <c>FOR UPDATE SKIP LOCKED</c> (multi-instance safe — N pods take disjoint batches),
/// publishes them to Kafka, and stamps them processed. At-least-once; the monolith's read-model
/// consumer is idempotent (upsert). Production: MassTransit / Debezium CDC.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Restaurant OutboxRelay started — draining restaurant.outbox_messages → Kafka (SKIP LOCKED).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();

                await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);

                var batch = await db.Set<OutboxMessage>()
                    .FromSqlInterpolated(
                        $@"SELECT * FROM restaurant.outbox_messages
                           WHERE ""ProcessedAt"" IS NULL
                           ORDER BY ""CreatedAt""
                           LIMIT {BatchSize}
                           FOR UPDATE SKIP LOCKED")
                    .ToListAsync(stoppingToken);

                foreach (var message in batch)
                {
                    // Re-attach the captured trace context so menu-updated → replica shows as one trace (ADR-041).
                    var parentCtx = TadkaTrace.ParseContext(message.TraceParent);
                    using var span = TadkaDiagnostics.ActivitySource.StartActivity(
                        $"outbox publish {message.Topic}", ActivityKind.Producer, parentCtx);
                    var headerTrace = span?.Id ?? message.TraceParent;
                    await producer.PublishRawAsync(message.Topic, message.Key, message.Payload, headerTrace, stoppingToken);
                    message.ProcessedAt = DateTime.UtcNow;
                }

                if (batch.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("Restaurant OutboxRelay published {Count} message(s).", batch.Count);
                }

                await tx.CommitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Restaurant OutboxRelay loop error — will retry."); }

            try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
