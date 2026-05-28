using Microsoft.EntityFrameworkCore;

namespace Tadka.Api.Data;

public class TadkaDbContext : DbContext
{
    public TadkaDbContext(DbContextOptions<TadkaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
