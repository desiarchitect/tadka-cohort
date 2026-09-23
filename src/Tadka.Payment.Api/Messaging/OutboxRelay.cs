using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tadka.Payment.Api.Data;
using Tadka.Telemetry;

namespace Tadka.Payment.Api.Messaging;

/// <summary>
/// Payment Outbox relay (ADR-028): same SKIP LOCKED claim as Ordering/Restaurant.
/// Drains <c>payment.outbox_messages</c> → Kafka (<c>payment-results</c>, <c>payment-refunded</c>, DLQ).
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Payment OutboxRelay started — draining payment.outbox_messages → Kafka (SKIP LOCKED).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

                // ADR-064: claim → publish → commit runs inside the execution strategy so a retrying
                // strategy (Database:EnableRetryOnFailure=true) can replay the whole unit. Flag off = runs once.
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear(); // a replay must not see rows tracked by the failed attempt
                    await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);

                    var batch = await db.Set<OutboxMessage>()
                        .FromSqlInterpolated(
                            $@"SELECT * FROM payment.outbox_messages
                               WHERE ""ProcessedAt"" IS NULL
                               ORDER BY ""CreatedAt""
                               LIMIT {BatchSize}
                               FOR UPDATE SKIP LOCKED")
                        .ToListAsync(stoppingToken);

                    foreach (var message in batch)
                    {
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
                        logger.LogInformation("Payment OutboxRelay published {Count} message(s).", batch.Count);
                    }

                    await tx.CommitAsync(stoppingToken);
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Payment OutboxRelay loop error — will retry."); }

            try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
