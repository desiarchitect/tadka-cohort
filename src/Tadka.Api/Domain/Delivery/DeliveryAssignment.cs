namespace Tadka.Api.Domain.Delivery;

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

public enum AssignmentStatus
{
    Assigned,
    PickedUp,
    Delivered,
    Cancelled
}
