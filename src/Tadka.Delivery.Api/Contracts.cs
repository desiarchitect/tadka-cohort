namespace Tadka.Delivery.Api;

public sealed record LocationRequest(double Latitude, double Longitude);
public sealed record TrackResponse(Guid OrderId, Guid AgentId, string AgentName, string Status, LocationRequest? Location);

/// <summary>The rider moves the delivery forward: <c>PickedUp</c>, <c>Delivered</c>, or <c>Cancelled</c>.</summary>
public sealed record DeliveryStatusRequest(string Status);
