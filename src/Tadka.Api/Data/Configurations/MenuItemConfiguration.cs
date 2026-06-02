using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Restaurants;

namespace Tadka.Api.Data.Configurations;

public class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> builder)
    {
        builder.ToTable("menu_items", "restaurant");

        builder.HasKey(mi => mi.Id);
        builder.Property(mi => mi.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(mi => mi.Name).IsRequired().HasMaxLength(100);
        builder.Property(mi => mi.Description).HasMaxLength(500);
        builder.Property(mi => mi.Category).HasMaxLength(50);
        builder.Property(mi => mi.IsAvailable).HasDefaultValue(true);
        builder.Property(mi => mi.IsVeg).HasDefaultValue(false);

        builder.OwnsOne(mi => mi.Price, money =>
        {
            money.Property(m => m.Amount).HasColumnName("price").HasColumnType("decimal(10,2)").IsRequired();
            money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("INR");
        });

        SeedMenuItems(builder);
    }

    private static void SeedMenuItems(EntityTypeBuilder<MenuItem> builder)
    {
        var meghana = new Guid("a1b2c3d4-0001-4000-8000-000000000001");
        var truffles = new Guid("a1b2c3d4-0002-4000-8000-000000000002");
        var vidyarthi = new Guid("a1b2c3d4-0003-4000-8000-000000000003");

        builder.HasData(
            // Meghana Foods (6 items)
            new { Id = new Guid("b1b2c3d4-0001-4000-8000-000000000001"), RestaurantId = meghana, Name = "Chicken Biryani", Description = "Hyderabadi-style dum biryani with tender chicken", Category = "Biryani", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0002-4000-8000-000000000002"), RestaurantId = meghana, Name = "Mutton Biryani", Description = "Slow-cooked mutton dum biryani with salan", Category = "Biryani", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0003-4000-8000-000000000003"), RestaurantId = meghana, Name = "Paneer Butter Masala", Description = "Creamy paneer in rich tomato gravy", Category = "Main Course", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0004-4000-8000-000000000004"), RestaurantId = meghana, Name = "Gutti Vankaya", Description = "Stuffed brinjal curry, Andhra style", Category = "Main Course", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0005-4000-8000-000000000005"), RestaurantId = meghana, Name = "Chicken 65", Description = "Spicy deep-fried chicken, Hyderabadi classic", Category = "Starters", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0006-4000-8000-000000000006"), RestaurantId = meghana, Name = "Curd Rice", Description = "Comfort food with tempered curd rice", Category = "Rice", IsAvailable = true, IsVeg = true },

            // Truffles (5 items)
            new { Id = new Guid("b1b2c3d4-0007-4000-8000-000000000007"), RestaurantId = truffles, Name = "Classic Smash Burger", Description = "Double-patty smash burger with house sauce", Category = "Burgers", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0008-4000-8000-000000000008"), RestaurantId = truffles, Name = "Truffle Special Burger", Description = "Signature burger with truffle mayo and caramelized onions", Category = "Burgers", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0009-4000-8000-000000000009"), RestaurantId = truffles, Name = "Loaded Fries", Description = "Crispy fries with cheese, jalapenos, and sour cream", Category = "Sides", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000a-4000-8000-000000000010"), RestaurantId = truffles, Name = "Chocolate Shake", Description = "Thick chocolate milkshake with whipped cream", Category = "Beverages", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000b-4000-8000-000000000011"), RestaurantId = truffles, Name = "Grilled Chicken Sandwich", Description = "Grilled chicken breast with lettuce and garlic aioli", Category = "Sandwiches", IsAvailable = true, IsVeg = false },

            // Vidyarthi Bhavan (5 items)
            new { Id = new Guid("b1b2c3d4-000c-4000-8000-000000000012"), RestaurantId = vidyarthi, Name = "Masala Dosa", Description = "Crispy dosa with spiced potato filling", Category = "Dosa", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000d-4000-8000-000000000013"), RestaurantId = vidyarthi, Name = "Benne Masala Dosa", Description = "Butter-roasted dosa, Karnataka specialty", Category = "Dosa", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000e-4000-8000-000000000014"), RestaurantId = vidyarthi, Name = "Idli Vada", Description = "Steamed idli with crispy medu vada and sambar", Category = "Breakfast", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000f-4000-8000-000000000015"), RestaurantId = vidyarthi, Name = "Kesari Bath", Description = "Sweet semolina halwa with ghee and cashews", Category = "Desserts", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0010-4000-8000-000000000016"), RestaurantId = vidyarthi, Name = "Filter Coffee", Description = "South Indian filter coffee, strong and frothy", Category = "Beverages", IsAvailable = true, IsVeg = true }
        );

        // Seed owned Price for each menu item
        builder.OwnsOne(mi => mi.Price).HasData(
            // Meghana Foods
            new { MenuItemId = new Guid("b1b2c3d4-0001-4000-8000-000000000001"), Amount = 299m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0002-4000-8000-000000000002"), Amount = 399m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0003-4000-8000-000000000003"), Amount = 249m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0004-4000-8000-000000000004"), Amount = 199m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0005-4000-8000-000000000005"), Amount = 229m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0006-4000-8000-000000000006"), Amount = 99m, Currency = "INR" },
            // Truffles
            new { MenuItemId = new Guid("b1b2c3d4-0007-4000-8000-000000000007"), Amount = 299m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0008-4000-8000-000000000008"), Amount = 449m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0009-4000-8000-000000000009"), Amount = 199m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000a-4000-8000-000000000010"), Amount = 179m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000b-4000-8000-000000000011"), Amount = 279m, Currency = "INR" },
            // Vidyarthi Bhavan
            new { MenuItemId = new Guid("b1b2c3d4-000c-4000-8000-000000000012"), Amount = 80m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000d-4000-8000-000000000013"), Amount = 99m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000e-4000-8000-000000000014"), Amount = 60m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-000f-4000-8000-000000000015"), Amount = 50m, Currency = "INR" },
            new { MenuItemId = new Guid("b1b2c3d4-0010-4000-8000-000000000016"), Amount = 30m, Currency = "INR" }
        );
    }
}
