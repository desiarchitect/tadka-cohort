namespace Tadka.Api.Infrastructure.Caching;

/// <summary>
/// Cache-aside facade (ADR-018). <see cref="GetOrSetAsync"/> returns the cached value, or runs the
/// factory (DB) on a miss and populates the cache — protected by a single-flight lock (ADR-019) so a
/// hot key's expiry doesn't stampede the database. Redis is a *performance* dependency, not a
/// correctness one: if it's absent or down, the implementation falls back to the factory (the DB).
/// </summary>
public interface ICacheService
{
    Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Delete a key (invalidation on write — delete-on-write, ADR-018).</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);
}
