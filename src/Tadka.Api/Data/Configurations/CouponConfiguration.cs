using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Configurations;

public class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    // Seeded demo coupon (Day 4 break kit). Reset between runs:
    //   UPDATE ordering.coupons SET redeemed = 0; DELETE FROM ordering.coupon_redemptions;
    public static readonly Guid Tadka50Id = Guid.Parse("c0000000-0001-4000-8000-000000000001");

    public void Configure(EntityTypeBuilder<Coupon> builder)
    {
        builder.ToTable("coupons", "ordering");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(50).IsRequired();
        builder.HasIndex(c => c.Code).IsUnique();
        builder.Property(c => c.MaxRedemptions).IsRequired();
        builder.Property(c => c.Redeemed).IsRequired();

        // Same optimistic token as orders (ADR-012) - it is the strategy the Day 4 break
        // shows failing-under-contention: correct, but a 409 storm on a single hot row.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasData(new Coupon
        {
            Id = Tadka50Id,
            Code = "TADKA50",
            MaxRedemptions = 100,
            Redeemed = 0
        });
    }
}

public class CouponRedemptionConfiguration : IEntityTypeConfiguration<CouponRedemption>
{
    public void Configure(EntityTypeBuilder<CouponRedemption> builder)
    {
        builder.ToTable("coupon_redemptions", "ordering");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.CouponId).IsRequired();
        builder.Property(r => r.CustomerId).IsRequired();
        builder.Property(r => r.RedeemedAt).HasDefaultValueSql("NOW()");

        builder.HasIndex(r => r.CouponId);

        // Deliberately NO unique (coupon_id, customer_id) constraint - the break demo fires the
        // same customer 50 times to show the row-level race, and real flash sales often allow
        // one-per-order rather than one-per-customer. The scarcity rule lives on the counter.
    }
}
