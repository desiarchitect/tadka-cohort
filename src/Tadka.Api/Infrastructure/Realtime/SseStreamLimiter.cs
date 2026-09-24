using System.Collections.Concurrent;

namespace Tadka.Api.Infrastructure.Realtime;

/// <summary>
/// ADR-064's per-user abuse cap for the live-tracking SSE stream (fix 5, item 5 from the Gemini
/// review): the SSE path (<c>OrderTrackingController.GetEvents</c>) is exempt from the gateway's
/// Front Door origin lock, so one logged-in user could otherwise hold an unbounded number of
/// concurrent streams against the gateway's public URL. This caps concurrent streams PER USER,
/// counted in-memory, per replica (a process restart or a second replica each get their own count
/// — an intentionally cheap, no-new-infra limit; see ADR-064 for the trade-off).
/// </summary>
public sealed class SseStreamLimiter
{
    private readonly ConcurrentDictionary<Guid, int> _countsByUser = new();
    private readonly int _maxPerUser;

    public SseStreamLimiter(int maxPerUser = 3) => _maxPerUser = maxPerUser;

    /// <summary>Current concurrent-stream count for a user (test/diagnostic hook).</summary>
    public int CurrentCount(Guid userId) => _countsByUser.TryGetValue(userId, out var n) ? n : 0;

    /// <summary>
    /// Attempts to claim one of this user's stream slots. Returns a disposable "lease" that MUST be
    /// disposed (in a finally, not just at the end of the happy path) when the stream ends, whether
    /// it ends normally, the client disconnects (OperationCanceledException), or any other exception
    /// unwinds — otherwise a dropped client permanently burns a slot and the user's next real
    /// connection gets wrongly 429'd forever.
    /// </summary>
    public bool TryAcquire(Guid userId, out IDisposable? lease)
    {
        var updated = _countsByUser.AddOrUpdate(userId, 1, (_, current) => current + 1);
        if (updated > _maxPerUser)
        {
            // Over the cap — immediately undo the increment we just made and refuse.
            _countsByUser.AddOrUpdate(userId, 0, (_, current) => Math.Max(0, current - 1));
            lease = null;
            return false;
        }

        lease = new Lease(this, userId);
        return true;
    }

    private void Release(Guid userId) =>
        _countsByUser.AddOrUpdate(userId, 0, (_, current) => Math.Max(0, current - 1));

    private sealed class Lease(SseStreamLimiter limiter, Guid userId) : IDisposable
    {
        private int _disposed; // guards against a double-release if Dispose is somehow called twice
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                limiter.Release(userId);
        }
    }
}
