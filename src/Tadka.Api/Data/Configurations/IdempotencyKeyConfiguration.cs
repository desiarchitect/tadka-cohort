using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Configurations;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys", "ordering");

        // The key IS the primary key — a unique constraint, so a replayed key can never
        // create a second order even under a concurrent race (the DB rejects the duplicate).
        builder.HasKey(k => k.Key);
        builder.Property(k => k.Key).HasMaxLength(100);

        builder.Property(k => k.OrderId).IsRequired();
        builder.Property(k => k.CreatedAt).HasDefaultValueSql("NOW()");
    }
}
