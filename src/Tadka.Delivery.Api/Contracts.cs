namespace Tadka.Delivery.Api;

public sealed record LocationRequest(double Latitude, double Longitude);
public sealed record TrackResponse(Guid OrderId, Guid AgentId, string AgentName, string Status, LocationRequest? Location);
