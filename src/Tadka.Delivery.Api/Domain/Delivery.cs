namespace Tadka.Delivery.Api.Domain;

/// <summary>A delivery rider. This service OWNS this type + its data (ADR-033) — its own DB.</summary>
public class DeliveryAgent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }

    /// <summary>The rider's login identity (the <c>sub</c> of the JWT the monolith issues them, role
    /// <c>DeliveryAgent</c>). This row is the dispatch record; the user is who the rider IS. Keeping them as
    /// two ids linked here is what lets this service answer "is the caller the rider on this order?" (ADR-031)
    /// without calling the monolith. Null = a rider with no login, who can't be authorised to post locations.</summary>
    public Guid? UserId { get; set; }
}

public enum AgentStatus { Offline, Available, OnDelivery }

/// <summary>The durable record of which agent took which order (ADR-034: history in Postgres, live location in Redis).</summary>
public class DeliveryAssignment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>Who placed the order, carried in on <c>order-confirmed</c> (event metadata, not a credential,
    /// ADR-031). Delivery has no copy of the orders table, so this is how <c>/track</c> checks ownership.
    /// Null on assignments made before this field existed → only Admin (or the rider) may read them.</summary>
    public Guid? CustomerId { get; set; }

    public AssignmentStatus Status { get; set; }
    public DateTime AssignedAt { get; set; }
    public DateTime? PickedUpAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public enum AssignmentStatus { Assigned, PickedUp, Delivered, Cancelled }

/// <summary>
/// A confirmed order that had no rider free when it arrived. Before this table existed, that case just
/// logged a warning and the Kafka message was marked done, so the order never got a rider. Now the work is
/// written down durably, and <c>PendingAssignmentSweeper</c> retries it until a rider frees up.
/// </summary>
public class PendingAssignment
{
    public Guid OrderId { get; set; }
    public Guid? CustomerId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
}

/// <summary>
/// "This order was cancelled" (its compensating refund settled). It does two jobs: the rider assigned to the
/// order is released, and if <c>order-confirmed</c> for it is still waiting in Kafka (this service was down, or
/// the two topics are read at different speeds) it is never given a rider at all. One row per order.
/// </summary>
public class CancelledOrder
{
    public Guid OrderId { get; set; }
    public DateTime CancelledAt { get; set; }
}

/// <summary>Inbox row (ADR-028): processed message-id → idempotent consumer (a redelivery assigns once).</summary>
public class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ConsumedAt { get; set; } = DateTime.UtcNow;
}
