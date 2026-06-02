using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", "ordering");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(o => o.CustomerId).IsRequired();
        builder.Property(o => o.RestaurantId).IsRequired();

        builder.Property(o => o.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(OrderStatus.Created);

        builder.OwnsOne(o => o.TotalAmount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("total_amount").HasColumnType("decimal(10,2)").IsRequired();
            money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("INR");
        });

        builder.OwnsOne(o => o.DeliveryAddress, addr =>
        {
            addr.Property(a => a.Line1).HasColumnName("delivery_address_line1").HasMaxLength(200);
            addr.Property(a => a.Line2).HasColumnName("delivery_address_line2").HasMaxLength(200);
            addr.Property(a => a.City).HasColumnName("delivery_address_city").HasMaxLength(50);
            addr.Property(a => a.Pincode).HasColumnName("delivery_address_pincode").HasMaxLength(10);
            addr.Property(a => a.Latitude).HasColumnName("delivery_latitude");
            addr.Property(a => a.Longitude).HasColumnName("delivery_longitude");
        });

        builder.Property(o => o.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(o => o.CancellationReason).HasMaxLength(500);

        builder.HasMany(o => o.Items).WithOne().HasForeignKey("OrderId").OnDelete(DeleteBehavior.Cascade);
    }
}
