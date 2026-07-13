using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Payment.Api.Domain;

namespace Tadka.Payment.Api.Data;

/// <summary>
/// The Payment service's data boundary (ADR-026). It owns the <c>payment</c> schema in its OWN physical
/// PostgreSQL — nothing else connects to it. Since Day 7 this context already existed and was scoped to
/// the payment schema; extraction made it point at a separate database (a connection-string change, not
/// a code change). Cross-service access is forbidden: Ordering learns outcomes via events, never a JOIN.
/// </summary>
public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Payment> Payments => Set<Domain.Payment>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payment");
        modelBuilder.ApplyConfiguration(new PaymentConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }
}

public class PaymentConfiguration : IEntityTypeConfiguration<Domain.Payment>
{
    public void Configure(EntityTypeBuilder<Domain.Payment> builder)
    {
        builder.ToTable("payments", "payment");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(p => p.OrderId).IsRequired();
        // One payment per order — the hard idempotency guard: a redelivered charge cannot insert a
        // second row, so it cannot double-charge.
        builder.HasIndex(p => p.OrderId).IsUnique();

        builder.Property(p => p.Method).IsRequired().HasMaxLength(20);
        builder.Property(p => p.GatewayReference).HasMaxLength(200);
        builder.Property(p => p.FailureReason).HasMaxLength(500);

        // Tokenized card data (ADR-046) — no PAN column exists here, by design. CardToken is a one-way
        // digest, not encrypted data; there is nothing to decrypt back to a card number.
        builder.Property(p => p.CardToken).HasMaxLength(24);
        builder.Property(p => p.CardLast4).HasMaxLength(4);

        builder.Property(p => p.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(PaymentStatus.Pending);

        builder.Property(p => p.CreatedAt).HasDefaultValueSql("NOW()");

        builder.OwnsOne(p => p.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("amount").HasColumnType("decimal(10,2)").IsRequired();
            money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("INR");
        });
    }
}
