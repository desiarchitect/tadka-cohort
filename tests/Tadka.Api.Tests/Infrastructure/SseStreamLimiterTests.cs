using Tadka.Api.Infrastructure.Realtime;

namespace Tadka.Api.Tests.Infrastructure;

/// <summary>
/// Pure unit tests for the per-user SSE concurrency cap (fix 5, ADR-064) — no database, no Redis.
/// </summary>
public class SseStreamLimiterTests
{
    [Fact]
    public void Acquires_are_allowed_up_to_the_configured_cap()
    {
        var limiter = new SseStreamLimiter(maxPerUser: 3);
        var user = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(user, out var lease1));
        Assert.True(limiter.TryAcquire(user, out var lease2));
        Assert.True(limiter.TryAcquire(user, out var lease3));
        Assert.Equal(3, limiter.CurrentCount(user));

        lease1?.Dispose();
        lease2?.Dispose();
        lease3?.Dispose();
    }

    [Fact]
    public void The_fourth_concurrent_stream_over_the_cap_is_refused()
    {
        var limiter = new SseStreamLimiter(maxPerUser: 3);
        var user = Guid.NewGuid();

        limiter.TryAcquire(user, out var lease1);
        limiter.TryAcquire(user, out var lease2);
        limiter.TryAcquire(user, out var lease3);

        var refused = limiter.TryAcquire(user, out var lease4);

        Assert.False(refused);
        Assert.Null(lease4);
        Assert.Equal(3, limiter.CurrentCount(user)); // the refused attempt must NOT count toward the total

        lease1?.Dispose();
        lease2?.Dispose();
        lease3?.Dispose();
    }

    [Fact]
    public void Disposing_the_lease_releases_the_slot_immediately()
    {
        var limiter = new SseStreamLimiter(maxPerUser: 1);
        var user = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(user, out var lease));
        Assert.Equal(1, limiter.CurrentCount(user));

        lease!.Dispose();

        Assert.Equal(0, limiter.CurrentCount(user));
        Assert.True(limiter.TryAcquire(user, out var secondLease)); // slot is available again
        secondLease?.Dispose();
    }

    [Fact]
    public void A_cancelled_or_aborted_stream_still_returns_the_counter_to_zero()
    {
        // Simulates OrderTrackingController.GetEvents's try/finally: acquire, then something throws
        // (e.g. OperationCanceledException from a dropped client) before the happy-path Dispose is
        // reached — the finally block must still release the slot, never leaking it.
        var limiter = new SseStreamLimiter(maxPerUser: 3);
        var user = Guid.NewGuid();

        limiter.TryAcquire(user, out var lease);
        try
        {
            throw new OperationCanceledException("client dropped mid-stream");
        }
        catch (OperationCanceledException)
        {
            // swallow, as the controller does
        }
        finally
        {
            lease?.Dispose();
        }

        Assert.Equal(0, limiter.CurrentCount(user));
    }

    [Fact]
    public void Different_users_have_independent_caps()
    {
        var limiter = new SseStreamLimiter(maxPerUser: 1);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(userA, out var leaseA));
        Assert.True(limiter.TryAcquire(userB, out var leaseB)); // B's cap is independent of A's

        leaseA?.Dispose();
        leaseB?.Dispose();
    }
}
