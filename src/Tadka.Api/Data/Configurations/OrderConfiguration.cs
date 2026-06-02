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

        // Performance indexes (ADR-014). Added only where a real query pattern justifies them —
        // adding one per column "just in case" is the zombie-index anti-pattern (every index taxes
        // every write). EF already creates the FK index on order_items(OrderId); customer_id and
        // created_at are bare Guids/timestamps (no FK, ADR-008), so they get no automatic index.
        //
        //  - GET /orders?customerId=… ORDER BY created_at DESC  → composite (customer_id, created_at DESC)
        //  - GET /orders            ORDER BY created_at DESC    → (created_at DESC)
        // Deliberately NOT indexed: orders(status), orders(restaurant_id) — no endpoint filters on
        // them yet, so an index would only slow writes. Add them the day a query needs them.
        builder.HasIndex(o => new { o.CustomerId, o.CreatedAt })
            .HasDatabaseName("ix_orders_customer_id_created_at")
            .IsDescending(false, true);
        builder.HasIndex(o => o.CreatedAt)
            .HasDatabaseName("ix_orders_created_at")
            .IsDescending(true);

        // Optimistic concurrency via PostgreSQL's xmin system column (ADR-012).
        // No extra column — Postgres already stamps every row with the id of the transaction that
        // last wrote it. We map that system column as a shadow concurrency token: if the row changed
        // between our read and our write, EF throws DbUpdateConcurrencyException → 409. This is what
        // prevents the "two concurrent status updates → lost update" break.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
