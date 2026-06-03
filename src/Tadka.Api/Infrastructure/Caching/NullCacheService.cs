namespace Tadka.Api.Infrastructure.Caching;

/// <summary>
/// No-op cache used when Redis is not configured (single-Postgres dev, the test suite). Every call
/// is a miss → the factory (DB) runs, nothing is stored. Keeps the app correct without Redis,
/// mirroring the Day-5 read-replica fallback (ADR-016).
/// </summary>
public sealed class NullCacheService : ICacheService
{
    public Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default)
        => factory();

    public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
}
