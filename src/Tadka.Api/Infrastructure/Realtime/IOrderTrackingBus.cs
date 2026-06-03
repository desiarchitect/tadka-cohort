namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// The live-tracking backplane (ADR-020). A status change <see cref="PublishAsync"/>es to channel
/// <c>order:{id}</c>; the SSE endpoint <see cref="SubscribeAsync"/>es to that channel and streams
/// each event to the connected client. Backed by Redis pub/sub so an update arriving at *any* app
/// instance reaches the connection held by *another* instance. <see cref="IsEnabled"/> is false when
/// Redis isn't configured (tracking then returns 503; caching still works via the no-op cache).
/// </summary>
public interface IOrderTrackingBus
{
    bool IsEnabled { get; }

    Task PublishAsync(OrderTrackingEvent trackingEvent, CancellationToken ct = default);

    /// <summary>Subscribe to one order's channel. Dispose to unsubscribe (on client disconnect).</summary>
    Task<IAsyncDisposable> SubscribeAsync(Guid orderId, Func<OrderTrackingEvent, Task> onEvent, CancellationToken ct = default);
}
