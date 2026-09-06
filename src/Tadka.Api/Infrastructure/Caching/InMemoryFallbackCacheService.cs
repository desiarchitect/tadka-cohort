using Microsoft.Extensions.Caching.Memory;

namespace Tadka.Api.Infrastructure.Caching;

/// <summary>
/// Day 6, scale-out beat (ADR-047 state-divergence demo): a REAL cache-aside implementation
/// (not a no-op like <see cref="NullCacheService"/>), but process-local — each replica keeps its
/// own private copy in <see cref="IMemoryCache"/>. This is a common real-world resilience
/// pattern ("if the distributed cache is down, fall back to something, anything") and it is
/// exactly the trap: three replicas behind a load balancer, each with its own private cache, no
/// longer agree on what the menu price is. Selected via `Cache:Mode=InMemory` (default is Redis
/// when configured, or the no-op <see cref="NullCacheService"/>) — never the shipped default.
/// </summary>
public sealed class InMemoryFallbackCacheService(IMemoryCache cache) : ICacheService
{
    private readonly IMemoryCache _cache = cache;
    // Per-key async lock so concurrent misses on THIS instance single-flight too (ADR-019's
    // idea, scoped to one process — it does nothing to stop three instances all refreshing
    // independently, which is precisely the point being demonstrated).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out T? cached))
            return cached;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached))
                return cached;

            var value = await factory();
            if (value is not null)
                _cache.Set(key, value, ttl);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        // Deletes only THIS instance's copy — the other replicas' private caches are untouched,
        // which is the other half of the divergence lesson (invalidation doesn't fan out either).
        _cache.Remove(key);
        return Task.CompletedTask;
    }
}
