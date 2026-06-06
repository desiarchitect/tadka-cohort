using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Restaurant.Api.Domain;

namespace Tadka.Restaurant.Api.Data;

/// <summary>The Restaurant service's own data boundary (ADR-036/026): the <c>restaurant</c> schema in its
/// OWN Postgres. Owns restaurants + menus + an Outbox for <c>menu-updated</c> (ADR-037).</summary>
public class RestaurantDbContext(DbContextOptions<RestaurantDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Restaurant> Restaurants => Set<Domain.Restaurant>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("restaurant");

        modelBuilder.Entity<Domain.Restaurant>(b =>
        {
            b.ToTable("restaurants", "restaurant");
            b.HasKey(r => r.Id); // app-generated Id (no store default) — lets a change stage its Outbox snapshot in the same txn
            b.Property(r => r.Name).IsRequired().HasMaxLength(100);
            b.Property(r => r.IsActive).HasDefaultValue(true);
            b.Property(r => r.AvgPrepTimeMinutes).HasDefaultValue(30);
            b.Property(r => r.CreatedAt).HasDefaultValueSql("NOW()");
            b.OwnsOne(r => r.Address, addr =>
            {
                addr.Property(a => a.Line1).HasColumnName("address_line1").HasMaxLength(200);
                addr.Property(a => a.Line2).HasColumnName("address_line2").HasMaxLength(200);
                addr.Property(a => a.City).HasColumnName("address_city").HasMaxLength(50);
                addr.Property(a => a.Pincode).HasColumnName("address_pincode").HasMaxLength(10);
                addr.Property(a => a.Latitude).HasColumnName("latitude");
                addr.Property(a => a.Longitude).HasColumnName("longitude");
            });
            b.HasMany(r => r.Menu).WithOne().HasForeignKey("RestaurantId").OnDelete(DeleteBehavior.Cascade);
            SeedRestaurants(b);
        });

        modelBuilder.Entity<MenuItem>(b =>
        {
            b.ToTable("menu_items", "restaurant");
            b.HasKey(mi => mi.Id);
            b.Property(mi => mi.Name).IsRequired().HasMaxLength(100);
            b.Property(mi => mi.Description).HasMaxLength(500);
            b.Property(mi => mi.Category).HasMaxLength(50);
            b.Property(mi => mi.IsAvailable).HasDefaultValue(true);
            b.Property(mi => mi.IsVeg).HasDefaultValue(false);
            b.OwnsOne(mi => mi.Price, money =>
            {
                money.Property(m => m.Amount).HasColumnName("price").HasColumnType("decimal(10,2)").IsRequired();
                money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("INR");
            });
            SeedMenuItems(b);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", "restaurant");
            b.HasKey(x => x.Id);
            b.Property(x => x.Topic).IsRequired().HasMaxLength(100);
            b.Property(x => x.Key).IsRequired().HasMaxLength(200);
            b.Property(x => x.Payload).IsRequired();
            b.Property(x => x.TraceParent).HasMaxLength(64);   // W3C traceparent (ADR-041), nullable
            b.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
            b.HasIndex(x => x.ProcessedAt);
        });
    }

    private static readonly Guid Meghana = new("a1b2c3d4-0001-4000-8000-000000000001");
    private static readonly Guid Truffles = new("a1b2c3d4-0002-4000-8000-000000000002");
    private static readonly Guid Vidyarthi = new("a1b2c3d4-0003-4000-8000-000000000003");
    private static readonly DateTime Seed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void SeedRestaurants(EntityTypeBuilder<Domain.Restaurant> b)
    {
        b.HasData(
            new { Id = Meghana, Name = "Meghana Foods", IsActive = true, AvgPrepTimeMinutes = 25, CreatedAt = Seed },
            new { Id = Truffles, Name = "Truffles", IsActive = true, AvgPrepTimeMinutes = 30, CreatedAt = Seed },
            new { Id = Vidyarthi, Name = "Vidyarthi Bhavan", IsActive = true, AvgPrepTimeMinutes = 20, CreatedAt = Seed });

        b.OwnsOne(r => r.Address).HasData(
            new { RestaurantId = Meghana, Line1 = "124, Near Forum Mall", Line2 = "Koramangala 5th Block", City = "Bangalore", Pincode = "560095", Latitude = 12.9352, Longitude = 77.6245 },
            new { RestaurantId = Truffles, Line1 = "96, 12th Main Road", Line2 = "HAL 2nd Stage, Indiranagar", City = "Bangalore", Pincode = "560038", Latitude = 12.9784, Longitude = 77.6408 },
            new { RestaurantId = Vidyarthi, Line1 = "32, Gandhi Bazaar Main Road", Line2 = "Basavanagudi", City = "Bangalore", Pincode = "560004", Latitude = 12.9454, Longitude = 77.5726 });
    }

    private static void SeedMenuItems(EntityTypeBuilder<MenuItem> b)
    {
        b.HasData(
            new { Id = new Guid("b1b2c3d4-0001-4000-8000-000000000001"), RestaurantId = Meghana, Name = "Chicken Biryani", Description = "Hyderabadi-style dum biryani with tender chicken", Category = "Biryani", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0002-4000-8000-000000000002"), RestaurantId = Meghana, Name = "Mutton Biryani", Description = "Slow-cooked mutton dum biryani with salan", Category = "Biryani", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0003-4000-8000-000000000003"), RestaurantId = Meghana, Name = "Paneer Butter Masala", Description = "Creamy paneer in rich tomato gravy", Category = "Main Course", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0004-4000-8000-000000000004"), RestaurantId = Meghana, Name = "Gutti Vankaya", Description = "Stuffed brinjal curry, Andhra style", Category = "Main Course", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0005-4000-8000-000000000005"), RestaurantId = Meghana, Name = "Chicken 65", Description = "Spicy deep-fried chicken, Hyderabadi classic", Category = "Starters", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0006-4000-8000-000000000006"), RestaurantId = Meghana, Name = "Curd Rice", Description = "Comfort food with tempered curd rice", Category = "Rice", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0007-4000-8000-000000000007"), RestaurantId = Truffles, Name = "Classic Smash Burger", Description = "Double-patty smash burger with house sauce", Category = "Burgers", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0008-4000-8000-000000000008"), RestaurantId = Truffles, Name = "Truffle Special Burger", Description = "Signature burger with truffle mayo and caramelized onions", Category = "Burgers", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0009-4000-8000-000000000009"), RestaurantId = Truffles, Name = "Loaded Fries", Description = "Crispy fries with cheese, jalapenos, and sour cream", Category = "Sides", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000a-4000-8000-000000000010"), RestaurantId = Truffles, Name = "Chocolate Shake", Description = "Thick chocolate milkshake with whipped cream", Category = "Beverages", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000b-4000-8000-000000000011"), RestaurantId = Truffles, Name = "Grilled Chicken Sandwich", Description = "Grilled chicken breast with lettuce and garlic aioli", Category = "Sandwiches", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-000c-4000-8000-000000000012"), RestaurantId = Vidyarthi, Name = "Masala Dosa", Description = "Crispy dosa with spiced potato filling", Category = "Dosa", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000d-4000-8000-000000000013"), RestaurantId = Vidyarthi, Name = "Benne Masala Dosa", Description = "Butter-roasted dosa, Karnataka specialty", Category = "Dosa", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000e-4000-8000-000000000014"), RestaurantId = Vidyarthi, Name = "Idli Vada", Description = "Steamed idli with crispy medu vada and sambar", Category = "Breakfast", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000f-4000-8000-000000000015"), RestaurantId = Vidyarthi, Name = "Kesari Bath", Description = "Sweet semolina halwa with ghee and cashews", Category = "Desserts", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0010-4000-8000-000000000016"), RestaurantId = Vidyarthi, Name = "Filter Coffee", Description = "South Indian filter coffee, strong and frothy", Category = "Beverages", IsAvailable = true, IsVeg = true });

        b.OwnsOne(mi => mi.Price).HasData(
            new { MenuItemId = new Guid("b1b2c3d4-0001-4000-8000-000000000001"), Amount = 299m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0002-4000-8000-000000000002"), Amount = 399m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0003-4000-8000-000000000003"), Amount = 249m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0004-4000-8000-000000000004"), Amount = 199m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0005-4000-8000-000000000005"), Amount = 229m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0006-4000-8000-000000000006"), Amount = 99m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0007-4000-8000-000000000007"), Amount = 299m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0008-4000-8000-000000000008"), Amount = 449m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0009-4000-8000-000000000009"), Amount = 199m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000a-4000-8000-000000000010"), Amount = 179m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000b-4000-8000-000000000011"), Amount = 279m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000c-4000-8000-000000000012"), Amount = 80m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000d-4000-8000-000000000013"), Amount = 99m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000e-4000-8000-000000000014"), Amount = 60m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000f-4000-8000-000000000015"), Amount = 50m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0010-4000-8000-000000000016"), Amount = 30m, Currency = "INR" });
    }
}
