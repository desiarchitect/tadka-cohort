using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Telemetry;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// The Outbox relay (ADR-028): claims unsent <see cref="OutboxMessage"/> rows, publishes them to Kafka,
/// and stamps them processed. At-least-once — if it crashes after publishing but before stamping, it
/// republishes; the consumer's Inbox dedups. This is the durable replacement for the Day-7/8 in-memory
/// queue: an event committed with the order can never be lost. (Production: MassTransit / Debezium CDC.)
///
/// MULTI-INSTANCE SAFETY (CTO review): the monolith may run on N pods, so N relays poll the same table.
/// A naive <c>WHERE ProcessedAt IS NULL ... LIMIT 50</c> would let every pod grab the SAME rows → duplicate
/// Kafka publishes (the Inbox keeps it *correct*, but it's wasteful + causes lock contention). We instead
/// claim each batch inside a transaction with <c>FOR UPDATE SKIP LOCKED</c>: the row locks are held until
/// commit, so a second pod skips the locked rows and grabs the next disjoint batch. This is the same
/// pattern a job-queue uses; leader-election or CDC (Debezium) are the heavier alternatives (see ADR-028).
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxRelay started — draining ordering.outbox_messages → Kafka (SKIP LOCKED claim).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

                // ADR-064: the whole claim → publish → commit unit runs inside the execution strategy. With
                // Database:EnableRetryOnFailure=true, EF's retrying strategy REFUSES a user-opened transaction
                // unless it can replay the entire unit after a transient fault (e.g. a Postgres failover).
                // With the flag off this is the default strategy and the lambda simply runs once. A replay
                // may republish rows already sent to Kafka: that is the same at-least-once the relay always
                // had (ADR-028), and the consumer's Inbox dedups it.
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear(); // a replay must not see rows tracked by the failed attempt

                    // Claim a disjoint batch: the transaction holds the FOR UPDATE row locks until commit,
                    // so concurrent relay instances SKIP LOCKED rows and never publish the same row twice.
                    await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);

                    var batch = await db.Set<OutboxMessage>()
                        .FromSqlInterpolated(
                            $@"SELECT * FROM ordering.outbox_messages
                               WHERE ""ProcessedAt"" IS NULL
                               ORDER BY ""CreatedAt""
                               LIMIT {BatchSize}
                               FOR UPDATE SKIP LOCKED")
                        .ToListAsync(stoppingToken);

                    foreach (var message in batch)
                    {
                        // Re-attach the trace context captured when the row was enqueued, so this async publish
                        // (the request span is long gone) rejoins the order's trace (ADR-041). The consumer reads
                        // this span's id from the Kafka header and hangs its work under the same trace.
                        var parentCtx = TadkaTrace.ParseContext(message.TraceParent);
                        using var span = TadkaDiagnostics.ActivitySource.StartActivity(
                            $"outbox publish {message.Topic}", ActivityKind.Producer, parentCtx);
                        var headerTrace = span?.Id ?? message.TraceParent;   // sampled span id, else the original
                        await producer.PublishRawAsync(message.Topic, message.Key, message.Payload, headerTrace, stoppingToken);
                        message.ProcessedAt = DateTime.UtcNow;
                    }

                    if (batch.Count > 0)
                    {
                        await db.SaveChangesAsync(stoppingToken);
                        logger.LogInformation("OutboxRelay claimed + published {Count} message(s).", batch.Count);
                    }

                    await tx.CommitAsync(stoppingToken); // releases the row locks
                });
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
