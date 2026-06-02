using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Restaurants;

namespace Tadka.Api.Data.Configurations;

public class RestaurantConfiguration : IEntityTypeConfiguration<Restaurant>
{
    public void Configure(EntityTypeBuilder<Restaurant> builder)
    {
        builder.ToTable("restaurants", "restaurant");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.IsActive).HasDefaultValue(true);
        builder.Property(r => r.AvgPrepTimeMinutes).HasDefaultValue(30);
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("NOW()");

        builder.OwnsOne(r => r.Address, addr =>
        {
            addr.Property(a => a.Line1).HasColumnName("address_line1").HasMaxLength(200);
            addr.Property(a => a.Line2).HasColumnName("address_line2").HasMaxLength(200);
            addr.Property(a => a.City).HasColumnName("address_city").HasMaxLength(50);
            addr.Property(a => a.Pincode).HasColumnName("address_pincode").HasMaxLength(10);
            addr.Property(a => a.Latitude).HasColumnName("latitude");
            addr.Property(a => a.Longitude).HasColumnName("longitude");
        });

        builder.HasMany(r => r.Menu).WithOne().HasForeignKey("RestaurantId").OnDelete(DeleteBehavior.Cascade);

        // Seed data: 3 iconic Bangalore restaurants
        builder.HasData(
            new
            {
                Id = new Guid("a1b2c3d4-0001-4000-8000-000000000001"),
                Name = "Meghana Foods",
                IsActive = true,
                AvgPrepTimeMinutes = 25,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = new Guid("a1b2c3d4-0002-4000-8000-000000000002"),
                Name = "Truffles",
                IsActive = true,
                AvgPrepTimeMinutes = 30,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = new Guid("a1b2c3d4-0003-4000-8000-000000000003"),
                Name = "Vidyarthi Bhavan",
                IsActive = true,
                AvgPrepTimeMinutes = 20,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );

        // Seed owned Address for each restaurant
        builder.OwnsOne(r => r.Address).HasData(
            new
            {
                RestaurantId = new Guid("a1b2c3d4-0001-4000-8000-000000000001"),
                Line1 = "124, Near Forum Mall",
                Line2 = "Koramangala 5th Block",
                City = "Bangalore",
                Pincode = "560095",
                Latitude = 12.9352,
                Longitude = 77.6245
            },
            new
            {
                RestaurantId = new Guid("a1b2c3d4-0002-4000-8000-000000000002"),
                Line1 = "96, 12th Main Road",
                Line2 = "HAL 2nd Stage, Indiranagar",
                City = "Bangalore",
                Pincode = "560038",
                Latitude = 12.9784,
                Longitude = 77.6408
            },
            new
            {
                RestaurantId = new Guid("a1b2c3d4-0003-4000-8000-000000000003"),
                Line1 = "32, Gandhi Bazaar Main Road",
                Line2 = "Basavanagudi",
                City = "Bangalore",
                Pincode = "560004",
                Latitude = 12.9454,
                Longitude = 77.5726
            }
        );
    }
}
