using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Payments;

namespace Tadka.Api.Data.Configurations;

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", "payment");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(p => p.OrderId).IsRequired();
        // One payment per order — the hard guard behind the idempotent processor (ADR-023):
        // a redelivered OrderPlaced cannot insert a second payment row, so it cannot double-charge.
        builder.HasIndex(p => p.OrderId).IsUnique();

        builder.Property(p => p.Method).IsRequired().HasMaxLength(20);
        builder.Property(p => p.GatewayReference).HasMaxLength(200);
        builder.Property(p => p.FailureReason).HasMaxLength(500);

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
