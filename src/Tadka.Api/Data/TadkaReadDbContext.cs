using Microsoft.EntityFrameworkCore;

namespace Tadka.Api.Data;

/// <summary>
/// Read-only EF context bound to the streaming <b>read replica</b> (ADR-016). It inherits the exact
/// same model/configuration as <see cref="TadkaDbContext"/> but is pointed at the replica connection
/// and runs <c>NoTracking</c> by default.
///
/// Use it for read-heavy GETs that tolerate a little replication lag — restaurant list, menu,
/// order history. Reads that must reflect a write the same user just made (read-your-writes, e.g.
/// fetching the order they just placed) must stay on the primary <see cref="TadkaDbContext"/>,
/// because the replica may be a few milliseconds behind. It is never migrated and never written to.
/// </summary>
public class TadkaReadDbContext : TadkaDbContext
{
    public TadkaReadDbContext(DbContextOptions<TadkaReadDbContext> options) : base(options)
    {
    }
}
