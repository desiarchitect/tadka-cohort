using Microsoft.EntityFrameworkCore;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;

namespace Tadka.Delivery.Api;

public sealed record AssignmentResult(Guid OrderId, Guid AgentId, string AgentName, string Status);

/// <summary>
/// Assigns an available rider to a confirmed order and records it (ADR-033). Idempotent: one assignment per
/// order (unique index) — a redelivered <c>order-confirmed</c> returns the existing assignment, doesn't
/// double-assign. The durable record lives in Postgres; the rider's live location lives in Redis (ADR-034).
/// </summary>
public sealed class DeliveryService(DeliveryDbContext db, ILogger<DeliveryService> logger)
{
    public async Task<AssignmentResult?> AssignAsync(Guid orderId, CancellationToken ct = default)
    {
        var existing = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.OrderId == orderId, ct);
        if (existing is not null)
        {
            var existingAgent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == existing.AgentId, ct);
            return new AssignmentResult(orderId, existing.AgentId, existingAgent?.Name ?? "", existing.Status.ToString());
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Status == AgentStatus.Available, ct);
        if (agent is null)
        {
            logger.LogWarning("No available rider for order {OrderId} — left unassigned.", orderId);
            return null;
        }

        var assignment = new DeliveryAssignment { OrderId = orderId, AgentId = agent.Id, Status = AssignmentStatus.Assigned, AssignedAt = DateTime.UtcNow };
        db.Assignments.Add(assignment);
        agent.Status = AgentStatus.OnDelivery;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // lost the unique-index race → idempotent
        {
            db.Entry(assignment).State = EntityState.Detached;
            var winner = await db.Assignments.AsNoTracking().FirstAsync(a => a.OrderId == orderId, ct);
            var winnerAgent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == winner.AgentId, ct);
            return new AssignmentResult(orderId, winner.AgentId, winnerAgent?.Name ?? "", winner.Status.ToString());
        }

        logger.LogInformation("🛵 Order {OrderId} assigned to rider {Agent} ({AgentId}).", orderId, agent.Name, agent.Id);
        return new AssignmentResult(orderId, agent.Id, agent.Name, assignment.Status.ToString());
    }
}
