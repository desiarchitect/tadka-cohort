using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Data.Configurations;

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", "ordering");

        builder.HasKey(oi => oi.Id);
        builder.Property(oi => oi.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(oi => oi.MenuItemId).IsRequired();
        builder.Property(oi => oi.Name).IsRequired().HasMaxLength(100);
        builder.Property(oi => oi.Quantity).IsRequired();
        builder.Property(oi => oi.SpecialInstructions).HasMaxLength(500);

        builder.OwnsOne(oi => oi.UnitPrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("unit_price").HasColumnType("decimal(10,2)").IsRequired();
            money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("INR");
        });
    }
}
