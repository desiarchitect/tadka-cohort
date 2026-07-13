namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// Used when Redis isn't configured. Publishing is a no-op (status changes still persist normally);
/// the SSE endpoint checks <see cref="IsEnabled"/> and returns 503 rather than subscribing.
/// </summary>
public sealed class NullOrderTrackingBus : IOrderTrackingBus
{
    public bool IsEnabled => false;

    public Task PublishAsync(OrderTrackingEvent trackingEvent, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IAsyncDisposable> SubscribeAsync(Guid orderId, Func<SequencedTrackingEvent, Task> onEvent, CancellationToken ct = default)
        => throw new NotSupportedException("Live order tracking requires Redis (ADR-020).");

    public Task<IReadOnlyList<SequencedTrackingEvent>> GetEventsSinceAsync(Guid orderId, long sinceSeq, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SequencedTrackingEvent>>([]);
}
