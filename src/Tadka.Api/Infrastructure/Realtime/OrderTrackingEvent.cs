namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// One server→client live-tracking message for an order (ADR-020). Serialized to the SSE stream.
/// <see cref="Latitude"/>/<see cref="Longitude"/> are null for a status-change event and populated for a
/// rider-location ping (ADR-036) — one event shape, no client-side special case to read either kind.
/// </summary>
public sealed record OrderTrackingEvent(
    Guid OrderId,
    string Status,
    string Message,
    DateTime Timestamp,
    double? Latitude = null,
    double? Longitude = null);
