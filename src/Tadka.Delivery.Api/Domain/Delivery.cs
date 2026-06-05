namespace Tadka.Delivery.Api.Domain;

/// <summary>A delivery rider. This service OWNS this type + its data (ADR-033) — its own DB.</summary>
public class DeliveryAgent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }
}

public enum AgentStatus { Offline, Available, OnDelivery }

/// <summary>The durable record of which agent took which order (ADR-034: history in Postgres, live location in Redis).</summary>
public class DeliveryAssignment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid AgentId { get; set; }
    public AssignmentStatus Status { get; set; }
    public DateTime AssignedAt { get; set; }
    public DateTime? PickedUpAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public enum AssignmentStatus { Assigned, PickedUp, Delivered, Cancelled }

/// <summary>Inbox row (ADR-028): processed message-id → idempotent consumer (a redelivery assigns once).</summary>
public class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ConsumedAt { get; set; } = DateTime.UtcNow;
}
