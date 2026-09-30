using Microsoft.EntityFrameworkCore;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;

namespace Tadka.Delivery.Api;

public sealed record AssignmentResult(Guid OrderId, Guid AgentId, string AgentName, string Status);

public enum StatusChangeOutcome { Ok, NotFound, InvalidTransition }

/// <summary>
/// Assigns an available rider to a confirmed order and records it (ADR-033). Idempotent: one assignment per
/// order (unique index) — a redelivered <c>order-confirmed</c> returns the existing assignment, doesn't
/// double-assign. The durable record lives in Postgres; the rider's live location lives in Redis (ADR-034).
///
/// Two things this class used to get wrong, and now doesn't:
/// 1. **No rider free → the order was dropped.** It logged a warning and returned null; the consumer then
///    stamped the Inbox and committed the offset, so nothing ever retried it. Now the order is PARKED in
///    <c>pending_assignments</c> and <see cref="RetryPendingAsync"/> (driven by <see cref="PendingAssignmentSweeper"/>)
///    assigns it once a rider frees up.
/// 2. **Riders were never freed.** Nothing ever set a rider back to Available, so a fresh database could
///    assign exactly three orders. <see cref="ChangeStatusAsync"/> (Delivered / Cancelled) now releases them.
/// Because a sweeper now assigns concurrently with the Kafka consumer, the rider claim is ATOMIC
/// (<c>UPDATE ... WHERE Status = 'Available'</c>): the order's unique index stops one order getting two riders,
/// and the claim stops one rider getting two orders.
/// </summary>
public sealed class DeliveryService(DeliveryDbContext db, ILogger<DeliveryService> logger)
{
    private const int CandidateLimit = 10;

    public async Task<AssignmentResult?> AssignAsync(
        Guid orderId,
        CancellationToken ct = default,
        Guid? customerId = null,
        double latitude = 0,
        double longitude = 0)
    {
        // ADR-064: with transient-fault retry on (the cloud failover demo), an explicit transaction must run
        // inside the execution strategy so the whole unit (claim + assignment) can be replayed together. With
        // the flag off this runs exactly once.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // a replay starts clean
            return await AssignOnceAsync(orderId, customerId, latitude, longitude, ct);
        });
    }

    private async Task<AssignmentResult?> AssignOnceAsync(Guid orderId, Guid? customerId, double latitude, double longitude, CancellationToken ct)
    {
        var existing = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.OrderId == orderId, ct);
        if (existing is not null)
        {
            var existingAgent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == existing.AgentId, ct);
            return new AssignmentResult(orderId, existing.AgentId, existingAgent?.Name ?? "", existing.Status.ToString());
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Still first-available (not nearest; see Day 11 Part 12), but each candidate is CLAIMED atomically.
        // Two concurrent assignments that both read "Lakshmi is Available" race on this UPDATE: Postgres runs
        // them one after the other on her row, the second re-checks the WHERE, finds OnDelivery, and gets 0
        // rows, so it moves on to the next candidate instead of double-booking her.
        var candidates = await db.Agents.AsNoTracking()
            .Where(a => a.Status == AgentStatus.Available)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name })
            .Take(CandidateLimit)
            .ToListAsync(ct);

        (Guid Id, string Name)? claimed = null;
        foreach (var candidate in candidates)
        {
            var rows = await db.Agents
                .Where(a => a.Id == candidate.Id && a.Status == AgentStatus.Available)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentStatus.OnDelivery), ct);
            if (rows == 1)
            {
                claimed = (candidate.Id, candidate.Name);
                break;
            }
        }

        if (claimed is null)
        {
            await tx.RollbackAsync(ct);
            await ParkAsync(orderId, customerId, latitude, longitude, ct);
            logger.LogWarning("No available rider for order {OrderId} — parked in pending_assignments; will retry when a rider frees up.", orderId);
            return null;
        }

        var parked = await db.PendingAssignments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, ct);
        var assignment = new DeliveryAssignment
        {
            OrderId = orderId,
            AgentId = claimed.Value.Id,
            CustomerId = customerId ?? parked?.CustomerId,
            Status = AssignmentStatus.Assigned,
            AssignedAt = DateTime.UtcNow
        };
        db.Assignments.Add(assignment);

        try
        {
            await db.PendingAssignments.Where(p => p.OrderId == orderId).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException) // lost the one-assignment-per-order race → idempotent
        {
            await tx.RollbackAsync(ct); // also undoes this call's rider claim, so the rider isn't stranded OnDelivery
            db.ChangeTracker.Clear();
            var winner = await db.Assignments.AsNoTracking().FirstAsync(a => a.OrderId == orderId, ct);
            var winnerAgent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == winner.AgentId, ct);
            return new AssignmentResult(orderId, winner.AgentId, winnerAgent?.Name ?? "", winner.Status.ToString());
        }

        logger.LogInformation("🛵 Order {OrderId} assigned to rider {Agent} ({AgentId}).", orderId, claimed.Value.Name, claimed.Value.Id);
        return new AssignmentResult(orderId, claimed.Value.Id, claimed.Value.Name, assignment.Status.ToString());
    }

    /// <summary>
    /// Tries to assign the oldest waiting orders. Returns the ones that got a rider (the caller publishes
    /// <c>delivery-assigned</c> for them). Stops early at the first order that still finds nobody free: if the
    /// oldest order can't get a rider, the newer ones can't either, and it keeps first-come-first-served order.
    /// </summary>
    public async Task<IReadOnlyList<AssignmentResult>> RetryPendingAsync(int batchSize, CancellationToken ct = default)
    {
        var waiting = await db.PendingAssignments.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        var assigned = new List<AssignmentResult>();
        foreach (var p in waiting)
        {
            var result = await AssignAsync(p.OrderId, ct, p.CustomerId, p.Latitude, p.Longitude);
            if (result is null)
                break;
            assigned.Add(result);
        }
        return assigned;
    }

    /// <summary>
    /// The rider (or ops) moves a delivery forward: Assigned → PickedUp → Delivered, or Assigned/PickedUp →
    /// Cancelled. Delivered and Cancelled RELEASE the rider back to Available, in the same SaveChanges as the
    /// status change, so a rider can never be "delivered but still busy".
    /// </summary>
    public async Task<(StatusChangeOutcome Outcome, string? Error)> ChangeStatusAsync(Guid orderId, AssignmentStatus next, CancellationToken ct = default)
    {
        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.OrderId == orderId, ct);
        if (assignment is null)
            return (StatusChangeOutcome.NotFound, null);

        var allowed = (assignment.Status, next) switch
        {
            (AssignmentStatus.Assigned, AssignmentStatus.PickedUp) => true,
            (AssignmentStatus.PickedUp, AssignmentStatus.Delivered) => true,
            (AssignmentStatus.Assigned or AssignmentStatus.PickedUp, AssignmentStatus.Cancelled) => true,
            _ => false
        };
        if (!allowed)
            return (StatusChangeOutcome.InvalidTransition, $"Cannot move a delivery from '{assignment.Status}' to '{next}'.");

        assignment.Status = next;
        if (next == AssignmentStatus.PickedUp) assignment.PickedUpAt = DateTime.UtcNow;
        if (next == AssignmentStatus.Delivered) assignment.DeliveredAt = DateTime.UtcNow;

        if (next is AssignmentStatus.Delivered or AssignmentStatus.Cancelled)
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == assignment.AgentId, ct);
            if (agent is not null && agent.Status == AgentStatus.OnDelivery)
                agent.Status = AgentStatus.Available;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Delivery for order {OrderId} is now {Status}.", orderId, next);
        return (StatusChangeOutcome.Ok, null);
    }

    private async Task ParkAsync(Guid orderId, Guid? customerId, double latitude, double longitude, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var bumped = await db.PendingAssignments
            .Where(p => p.OrderId == orderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Attempts, p => p.Attempts + 1)
                .SetProperty(p => p.LastAttemptAt, now), ct);
        if (bumped == 1)
            return; // already waiting (a sweeper retry, or a redelivered event)

        db.PendingAssignments.Add(new PendingAssignment
        {
            OrderId = orderId,
            CustomerId = customerId,
            Latitude = latitude,
            Longitude = longitude,
            CreatedAt = now,
            Attempts = 1,
            LastAttemptAt = now
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // a concurrent call parked it first — same outcome
        {
            db.ChangeTracker.Clear();
        }
    }
}
