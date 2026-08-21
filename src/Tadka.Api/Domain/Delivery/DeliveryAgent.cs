using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Delivery;

public enum AgentStatus
{
    Available,
    OnDelivery,
    Offline
}

// Aggregate Root. An agent's lifecycle (sign up, go online/offline, move) is
// INDEPENDENT of any single delivery — which is exactly why it is a separate
// aggregate from DeliveryAssignment. See the Day-2 aggregate-boundaries lesson.
public class DeliveryAgent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }
    public GeoLocation CurrentLocation { get; set; } = null!;
}
