using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data.Configurations;
using Tadka.Api.Domain.Payments;

namespace Tadka.Api.Data;

/// <summary>
/// The Payment module's own data boundary (ADR-022). It owns the <c>payment</c> schema and its OWN
/// EF migration history (<c>payment.__EFMigrationsHistory</c>) — Ordering's <see cref="TadkaDbContext"/>
/// no longer maps <see cref="Payment"/> at all. Today this points at the SAME physical Postgres as the
/// core context (logical separation); the PHYSICAL split (a separate database) is the Day-8 extraction.
/// That is the honest modular-monolith step: reason about payment data in isolation before paying to move it.
/// </summary>
public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
    {
    }

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payment");
        // Only the Payment module's configuration — this context knows nothing of orders/restaurants.
        modelBuilder.ApplyConfiguration(new PaymentConfiguration());
    }
}
