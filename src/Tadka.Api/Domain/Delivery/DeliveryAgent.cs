using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Delivery;

public class DeliveryAgent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }
    public GeoLocation? CurrentLocation { get; set; }
}

public enum AgentStatus
{
    Offline,
    Available,
    OnDelivery
}
