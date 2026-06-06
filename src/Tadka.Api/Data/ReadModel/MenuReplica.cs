using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tadka.Api.Data.ReadModel;

/// <summary>
/// A local, read-only replica of a restaurant — Ordering's OWN data (ADR-037, event-carried state
/// transfer). Fed by the Restaurant service's <c>menu-updated</c> events (ADR-036). Order pricing reads
/// this, never a cross-service call, so orders keep flowing even when Restaurant is down. It is a *cache*
/// of data the Restaurant service owns — no cross-schema FK (ADR-008).
/// </summary>
public class RestaurantReplica
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A menu item in the local price replica (ADR-037). The only fields Ordering needs to price.</summary>
public class MenuItemReplica
{
    public Guid MenuItemId { get; set; }
    public Guid RestaurantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = "INR";
    public bool IsAvailable { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class RestaurantReplicaConfiguration : IEntityTypeConfiguration<RestaurantReplica>
{
    public void Configure(EntityTypeBuilder<RestaurantReplica> b)
    {
        b.ToTable("restaurant_replica", "ordering");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(100);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("NOW()");
        Seed(b);
    }

    // Seeded with the canonical 3 restaurants so the replica works out of the box (and tests are
    // deterministic) before any event arrives — this is exactly what the online backfill (ADR-038) does
    // for the *current* menu; events keep it fresh thereafter.
    private static void Seed(EntityTypeBuilder<RestaurantReplica> b)
    {
        var seed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        b.HasData(
            new RestaurantReplica { Id = new("a1b2c3d4-0001-4000-8000-000000000001"), Name = "Meghana Foods", IsActive = true, UpdatedAt = seed },
            new RestaurantReplica { Id = new("a1b2c3d4-0002-4000-8000-000000000002"), Name = "Truffles", IsActive = true, UpdatedAt = seed },
            new RestaurantReplica { Id = new("a1b2c3d4-0003-4000-8000-000000000003"), Name = "Vidyarthi Bhavan", IsActive = true, UpdatedAt = seed });
    }
}

public class MenuItemReplicaConfiguration : IEntityTypeConfiguration<MenuItemReplica>
{
    public void Configure(EntityTypeBuilder<MenuItemReplica> b)
    {
        b.ToTable("menu_replica", "ordering");
        b.HasKey(x => x.MenuItemId);
        b.Property(x => x.Name).IsRequired().HasMaxLength(100);
        b.Property(x => x.PriceAmount).HasColumnType("decimal(10,2)");
        b.Property(x => x.PriceCurrency).HasMaxLength(3).HasDefaultValue("INR");
        b.Property(x => x.IsAvailable).HasDefaultValue(true);
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("NOW()");
        b.HasIndex(x => x.RestaurantId); // price-by-restaurant lookup
        Seed(b);
    }

    private static void Seed(EntityTypeBuilder<MenuItemReplica> b)
    {
        var seed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var meghana = new Guid("a1b2c3d4-0001-4000-8000-000000000001");
        var truffles = new Guid("a1b2c3d4-0002-4000-8000-000000000002");
        var vidyarthi = new Guid("a1b2c3d4-0003-4000-8000-000000000003");

        b.HasData(
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0001-4000-8000-000000000001"), RestaurantId = meghana, Name = "Chicken Biryani", PriceAmount = 299m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0002-4000-8000-000000000002"), RestaurantId = meghana, Name = "Mutton Biryani", PriceAmount = 399m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0003-4000-8000-000000000003"), RestaurantId = meghana, Name = "Paneer Butter Masala", PriceAmount = 249m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0004-4000-8000-000000000004"), RestaurantId = meghana, Name = "Gutti Vankaya", PriceAmount = 199m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0005-4000-8000-000000000005"), RestaurantId = meghana, Name = "Chicken 65", PriceAmount = 229m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0006-4000-8000-000000000006"), RestaurantId = meghana, Name = "Curd Rice", PriceAmount = 99m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0007-4000-8000-000000000007"), RestaurantId = truffles, Name = "Classic Smash Burger", PriceAmount = 299m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0008-4000-8000-000000000008"), RestaurantId = truffles, Name = "Truffle Special Burger", PriceAmount = 449m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0009-4000-8000-000000000009"), RestaurantId = truffles, Name = "Loaded Fries", PriceAmount = 199m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-000a-4000-8000-000000000010"), RestaurantId = truffles, Name = "Chocolate Shake", PriceAmount = 179m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-000b-4000-8000-000000000011"), RestaurantId = truffles, Name = "Grilled Chicken Sandwich", PriceAmount = 279m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-000c-4000-8000-000000000012"), RestaurantId = vidyarthi, Name = "Masala Dosa", PriceAmount = 80m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-000d-4000-8000-000000000013"), RestaurantId = vidyarthi, Name = "Benne Masala Dosa", PriceAmount = 99m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-000e-4000-8000-000000000014"), RestaurantId = vidyarthi, Name = "Idli Vada", PriceAmount = 60m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-000f-4000-8000-000000000015"), RestaurantId = vidyarthi, Name = "Kesari Bath", PriceAmount = 50m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed },
            new MenuItemReplica { MenuItemId = new("b1b2c3d4-0010-4000-8000-000000000016"), RestaurantId = vidyarthi, Name = "Filter Coffee", PriceAmount = 30m, PriceCurrency = "INR", IsAvailable = true, UpdatedAt = seed });
    }
}
