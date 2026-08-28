using Microsoft.EntityFrameworkCore;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data;

// Reference data for the Day-3 live demo. GUIDs are stable so the runbook
// and teaching script can paste them. Delivery agents are not seeded — there
// is no delivery API today.
internal static class DemoSeed
{
    internal static readonly Guid MeghanaId = new("a1b2c3d4-0001-4000-8000-000000000001");
    internal static readonly Guid TrufflesId = new("a1b2c3d4-0002-4000-8000-000000000002");
    internal static readonly Guid VidyarthiId = new("a1b2c3d4-0003-4000-8000-000000000003");
    internal static readonly Guid ChickenBiryaniId = new("b1b2c3d4-0001-4000-8000-000000000001");
    internal static readonly Guid PriyaId = new("c1b2c3d4-0001-4000-8000-000000000001");

    internal static void Apply(ModelBuilder modelBuilder)
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Restaurant>().HasData(
            new { Id = MeghanaId, Name = "Meghana Foods", IsActive = true, AvgPrepTimeMinutes = 25, CreatedAt = created },
            new { Id = TrufflesId, Name = "Truffles", IsActive = true, AvgPrepTimeMinutes = 30, CreatedAt = created },
            new { Id = VidyarthiId, Name = "Vidyarthi Bhavan", IsActive = true, AvgPrepTimeMinutes = 20, CreatedAt = created });

        modelBuilder.Entity<Restaurant>().OwnsOne(r => r.Address).HasData(
            new { RestaurantId = MeghanaId, Line1 = "124, Near Forum Mall", Line2 = "Koramangala 5th Block", City = "Bangalore", Pincode = "560095", Latitude = 12.9352, Longitude = 77.6245 },
            new { RestaurantId = TrufflesId, Line1 = "96, 12th Main Road", Line2 = "HAL 2nd Stage, Indiranagar", City = "Bangalore", Pincode = "560038", Latitude = 12.9784, Longitude = 77.6408 },
            new { RestaurantId = VidyarthiId, Line1 = "32, Gandhi Bazaar Main Road", Line2 = "Basavanagudi", City = "Bangalore", Pincode = "560004", Latitude = 12.9454, Longitude = 77.5726 });

        modelBuilder.Entity<MenuItem>().HasData(
            new { Id = ChickenBiryaniId, RestaurantId = MeghanaId, Name = "Chicken Biryani", Description = "Hyderabadi-style dum biryani with tender chicken", Category = "Biryani", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0002-4000-8000-000000000002"), RestaurantId = MeghanaId, Name = "Mutton Biryani", Description = "Slow-cooked mutton dum biryani with salan", Category = "Biryani", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0003-4000-8000-000000000003"), RestaurantId = MeghanaId, Name = "Paneer Butter Masala", Description = "Creamy paneer in rich tomato gravy", Category = "Main Course", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0004-4000-8000-000000000004"), RestaurantId = MeghanaId, Name = "Gutti Vankaya", Description = "Stuffed brinjal curry, Andhra style", Category = "Main Course", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0005-4000-8000-000000000005"), RestaurantId = MeghanaId, Name = "Chicken 65", Description = "Spicy deep-fried chicken, Hyderabadi classic", Category = "Starters", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0006-4000-8000-000000000006"), RestaurantId = MeghanaId, Name = "Curd Rice", Description = "Comfort food with tempered curd rice", Category = "Rice", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0007-4000-8000-000000000007"), RestaurantId = TrufflesId, Name = "Classic Smash Burger", Description = "Double-patty smash burger with house sauce", Category = "Burgers", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0008-4000-8000-000000000008"), RestaurantId = TrufflesId, Name = "Truffle Special Burger", Description = "Signature burger with truffle mayo and caramelized onions", Category = "Burgers", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-0009-4000-8000-000000000009"), RestaurantId = TrufflesId, Name = "Loaded Fries", Description = "Crispy fries with cheese, jalapenos, and sour cream", Category = "Sides", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000a-4000-8000-000000000010"), RestaurantId = TrufflesId, Name = "Chocolate Shake", Description = "Thick chocolate milkshake with whipped cream", Category = "Beverages", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000b-4000-8000-000000000011"), RestaurantId = TrufflesId, Name = "Grilled Chicken Sandwich", Description = "Grilled chicken breast with lettuce and garlic aioli", Category = "Sandwiches", IsAvailable = true, IsVeg = false },
            new { Id = new Guid("b1b2c3d4-000c-4000-8000-000000000012"), RestaurantId = VidyarthiId, Name = "Masala Dosa", Description = "Crispy dosa with spiced potato filling", Category = "Dosa", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000d-4000-8000-000000000013"), RestaurantId = VidyarthiId, Name = "Benne Masala Dosa", Description = "Butter-roasted dosa, Karnataka specialty", Category = "Dosa", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000e-4000-8000-000000000014"), RestaurantId = VidyarthiId, Name = "Idli Vada", Description = "Steamed idli with crispy medu vada and sambar", Category = "Breakfast", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-000f-4000-8000-000000000015"), RestaurantId = VidyarthiId, Name = "Kesari Bath", Description = "Sweet semolina halwa with ghee and cashews", Category = "Desserts", IsAvailable = true, IsVeg = true },
            new { Id = new Guid("b1b2c3d4-0010-4000-8000-000000000016"), RestaurantId = VidyarthiId, Name = "Filter Coffee", Description = "South Indian filter coffee, strong and frothy", Category = "Beverages", IsAvailable = true, IsVeg = true });

        modelBuilder.Entity<MenuItem>().OwnsOne(m => m.Price).HasData(
            new { MenuItemId = ChickenBiryaniId, Amount = 299m, Currency = "INR" },
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

        modelBuilder.Entity<User>().HasData(new User
        {
            Id = PriyaId,
            Name = "Priya Sharma",
            Email = "priya@tadka.test",
            Phone = "+919876500001",
            Role = UserRole.Customer,
            CreatedAt = created
        });
    }
}
