namespace Tadka.Api.Domain.Delivery;

public enum AssignmentStatus
{
    Assigned,
    PickedUp,
    Delivered,
    Cancelled
}

// Aggregate Root, separate from DeliveryAgent. Its lifecycle is tied to ONE
// order (assigned -> picked up -> delivered), not to the agent's profile.
// It references the agent and the order by ID only.
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
