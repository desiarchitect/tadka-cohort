using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Data.ReadModel;
using Tadka.Telemetry;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// The read-model updater (ADR-037, event-carried state transfer): consumes <c>menu-updated</c> from the
/// Restaurant service and upserts Ordering's local price replica (<c>ordering.restaurant_replica</c> +
/// <c>ordering.menu_replica</c>). Because the event carries the FULL snapshot, the upsert is naturally
/// idempotent (last-write-wins) — a redelivery just re-applies the same state, so no Inbox is needed.
/// Order pricing then reads the replica and stays available even when Restaurant is down.
///
/// A snapshot that fails to apply is never skipped: the consumer seeks back to it and retries a bounded number
/// of times, then quarantines it on <see cref="Topics.MenuUpdatedDlq"/> and moves on (ADR-051). Without the
/// seek, committing the NEXT message's offset would silently commit past the failed one and the replica would
/// stay stale until that restaurant happened to change again.
/// </summary>
public sealed class MenuUpdatedConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    IOptions<KafkaOptions> options,
    ILogger<MenuUpdatedConsumer> logger) : BackgroundService
{
    private readonly PoisonMessageTracker _poison = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroup + "-menu",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }.ApplySasl(options.Value);

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.MenuUpdated);
        logger.LogInformation("MenuUpdatedConsumer subscribed to {Topic}.", Topics.MenuUpdated);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            Activity? activity = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;
                activity = TadkaDiagnostics.ActivitySource.StartActivity(   // rejoin the trace (ADR-041)
                    $"consume {Topics.MenuUpdated}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));
                var applied = await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
                _poison.Clear(cr.TopicPartitionOffset);

                // Replica-lag gauge (ADR-063): use Kafka's own per-message timestamp as "the event's own
                // timestamp" — no wire-contract change needed. Only advance on an actual apply; a
                // null/undeserializable message (logged, not applied) shouldn't make the replica look fresher.
                if (applied)
                    Interlocked.Exchange(ref TadkaDiagnostics.LastMenuReplicaAppliedEventUnixMs, cr.Message.Timestamp.UnixTimestampMs);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                logger.LogError(ex, "MenuUpdatedConsumer consume error.");
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

    private async Task HandlePoisonAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> cr, Exception ex, CancellationToken ct)
    {
        if (!_poison.RecordFailureAndShouldDlq(cr.TopicPartitionOffset))
        {
            logger.LogWarning(ex, "menu-updated at {Offset} failed — will retry (idempotent upsert).", cr.TopicPartitionOffset);
            // Consume() advances the fetch position on every call regardless of commit, so without this
            // explicit seek the loop would simply move on to the NEXT message and silently drop this one.
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "menu-updated at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.MenuUpdatedDlq, cr.Message.Key,
                new DlqMessage(Topics.MenuUpdated, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            // Could not quarantine it (broker down). Do NOT commit past it and do NOT let this escape the loop
            // (an exception out of a catch block would stop the whole host): rewind and try again later.
            logger.LogError(dlqEx, "Could not publish menu-updated at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

        consumer.Commit(cr); // now genuinely unblock: this offset is quarantined, not silently lost
        _poison.Clear(cr.TopicPartitionOffset);
    }

    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

    /// <returns>true if the snapshot was actually applied to the replica (used to drive the ADR-063 lag gauge).</returns>
    private async Task<bool> HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<RestaurantSnapshotMessage>(value);
        if (msg is null) return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();

        // Upsert the restaurant row.
        var existing = await db.Set<RestaurantReplica>().FirstOrDefaultAsync(r => r.Id == msg.RestaurantId, ct);
        if (existing is null)
            db.Set<RestaurantReplica>().Add(new RestaurantReplica { Id = msg.RestaurantId, Name = msg.Name, IsActive = msg.IsActive, UpdatedAt = DateTime.UtcNow });
        else
        {
            existing.Name = msg.Name;
            existing.IsActive = msg.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        // Replace this restaurant's menu rows with the snapshot (full-state, idempotent).
        var old = await db.Set<MenuItemReplica>().Where(m => m.RestaurantId == msg.RestaurantId).ToListAsync(ct);
        db.Set<MenuItemReplica>().RemoveRange(old);
        db.Set<MenuItemReplica>().AddRange(msg.Menu.Select(m => new MenuItemReplica
        {
            MenuItemId = m.MenuItemId,
            RestaurantId = msg.RestaurantId,
            Name = m.Name,
            PriceAmount = m.PriceAmount,
            PriceCurrency = m.PriceCurrency,
            IsAvailable = m.IsAvailable,
            UpdatedAt = DateTime.UtcNow
        }));

        await db.SaveChangesAsync(ct);
        logger.LogInformation("menu-updated applied for restaurant {RestaurantId} ({Count} items).", msg.RestaurantId, msg.Menu.Count);
        return true;
    }
}
