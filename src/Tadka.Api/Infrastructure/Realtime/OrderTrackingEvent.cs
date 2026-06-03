namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>One server→client live-tracking message for an order (ADR-020). Serialized to the SSE stream.</summary>
public sealed record OrderTrackingEvent(Guid OrderId, string Status, string Message, DateTime Timestamp);
