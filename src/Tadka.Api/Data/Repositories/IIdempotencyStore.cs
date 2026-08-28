using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Repositories;

public interface IIdempotencyStore
{
    /// <summary>Returns the order a previously-seen key created, or null if the key is new.</summary>
    Task<Guid?> FindOrderIdAsync(string key);

    /// <summary>Stages a key→order mapping. Committed by the order's SaveChanges (same transaction).</summary>
    void Record(string key, Guid orderId);
}

public class IdempotencyStore(TadkaDbContext db) : IIdempotencyStore
{
    private readonly TadkaDbContext _db = db;

    public async Task<Guid?> FindOrderIdAsync(string key)
    {
        var record = await _db.IdempotencyKeys.FindAsync(key);
        return record?.OrderId;
    }

    public void Record(string key, Guid orderId)
    {
        _db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key,
            OrderId = orderId,
            CreatedAt = DateTime.UtcNow
        });
    }
}
