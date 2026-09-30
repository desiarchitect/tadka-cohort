using Microsoft.Extensions.Options;
using Tadka.Delivery.Api.Messaging;

namespace Tadka.Delivery.Api;

/// <summary>Delivery-service tuning (bound from the <c>Delivery</c> config section).</summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>How often waiting (parked) orders are retried. 0 disables the sweeper (the test suite drives
    /// <see cref="DeliveryService.RetryPendingAsync"/> directly instead, so nothing races the assertions).</summary>
    public int PendingRetrySeconds { get; set; } = 5;

    /// <summary>How many of the oldest waiting orders one sweep tries.</summary>
    public int PendingBatchSize { get; set; } = 20;
}

/// <summary>
/// Retries orders that arrived when no rider was free (<c>pending_assignments</c>). When a rider is released
/// (<c>PATCH /deliveries/{orderId}/status</c> → Delivered/Cancelled), the oldest waiting order gets them on the
/// next tick. Each order that gets a rider is announced on <c>delivery-assigned</c>, same as the consumer does.
///
/// Honest limit: <c>delivery-assigned</c> is published straight to Kafka after the assignment commits (no
/// Delivery-side Outbox yet), the same dual-write the consumer path already accepts. A crash between the
/// commit and the publish loses that one announcement, not the assignment.
/// </summary>
public sealed class PendingAssignmentSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliveryOptions> options,
    ILogger<PendingAssignmentSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.PendingRetrySeconds;
        if (seconds <= 0)
        {
            logger.LogInformation("PendingAssignmentSweeper disabled (Delivery:PendingRetrySeconds = {Seconds}).", seconds);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "PendingAssignmentSweeper sweep failed — will try again next tick.");
            }
        }
    }

    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<DeliveryService>();
        var assigned = await delivery.RetryPendingAsync(options.Value.PendingBatchSize, ct);

        var producer = scope.ServiceProvider.GetService<KafkaProducer>(); // null when Kafka is off (tests, single-process dev)
        foreach (var a in assigned)
        {
            logger.LogInformation("Waiting order {OrderId} finally got rider {Agent}.", a.OrderId, a.AgentName);
            if (producer is not null)
                await producer.PublishAsync(Topics.DeliveryAssigned, a.OrderId.ToString(),
                    new DeliveryAssignedMessage(Guid.NewGuid(), a.OrderId, a.AgentId, a.AgentName), ct);
        }
        return assigned.Count;
    }
}
