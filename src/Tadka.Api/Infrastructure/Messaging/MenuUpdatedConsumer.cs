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
/// </summary>
public sealed class MenuUpdatedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<MenuUpdatedConsumer> logger) : BackgroundService
{
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
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;
                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(   // rejoin the trace (ADR-041)
                    $"consume {Topics.MenuUpdated}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));
                var applied = await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);

                // Replica-lag gauge (ADR-063): use Kafka's own per-message timestamp as "the event's own
                // timestamp" — no wire-contract change needed. Only advance on an actual apply; a
                // null/undeserializable message (logged, not applied) shouldn't make the replica look fresher.
                if (applied)
                    Interlocked.Exchange(ref TadkaDiagnostics.LastMenuReplicaAppliedEventUnixMs, cr.Message.Timestamp.UnixTimestampMs);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "MenuUpdatedConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "MenuUpdatedConsumer handler error — will reprocess (idempotent upsert)."); }
        }

        consumer.Close();
    }, stoppingToken);

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
