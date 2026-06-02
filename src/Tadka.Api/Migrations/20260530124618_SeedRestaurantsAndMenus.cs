using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeedRestaurantsAndMenus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "restaurants",
                columns: new[] { "Id", "AvgPrepTimeMinutes", "CreatedAt", "IsActive", "Name", "address_city", "latitude", "address_line1", "address_line2", "longitude", "address_pincode" },
                values: new object[,]
                {
                    { new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 25, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Meghana Foods", "Bangalore", 12.9352, "124, Near Forum Mall", "Koramangala 5th Block", 77.624499999999998, "560095" },
                    { new Guid("a1b2c3d4-0002-4000-8000-000000000002"), 30, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Truffles", "Bangalore", 12.978400000000001, "96, 12th Main Road", "HAL 2nd Stage, Indiranagar", 77.640799999999999, "560038" },
                    { new Guid("a1b2c3d4-0003-4000-8000-000000000003"), 20, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Vidyarthi Bhavan", "Bangalore", 12.945399999999999, "32, Gandhi Bazaar Main Road", "Basavanagudi", 77.572599999999994, "560004" }
                });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "Name", "RestaurantId", "price", "currency" },
                values: new object[,]
                {
                    { new Guid("b1b2c3d4-0001-4000-8000-000000000001"), "Biryani", "Hyderabadi-style dum biryani with tender chicken", true, "Chicken Biryani", new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 299m, "INR" },
                    { new Guid("b1b2c3d4-0002-4000-8000-000000000002"), "Biryani", "Slow-cooked mutton dum biryani with salan", true, "Mutton Biryani", new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 399m, "INR" }
                });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "IsVeg", "Name", "RestaurantId", "price", "currency" },
                values: new object[,]
                {
                    { new Guid("b1b2c3d4-0003-4000-8000-000000000003"), "Main Course", "Creamy paneer in rich tomato gravy", true, true, "Paneer Butter Masala", new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 249m, "INR" },
                    { new Guid("b1b2c3d4-0004-4000-8000-000000000004"), "Main Course", "Stuffed brinjal curry, Andhra style", true, true, "Gutti Vankaya", new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 199m, "INR" }
                });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "Name", "RestaurantId", "price", "currency" },
                values: new object[] { new Guid("b1b2c3d4-0005-4000-8000-000000000005"), "Starters", "Spicy deep-fried chicken, Hyderabadi classic", true, "Chicken 65", new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 229m, "INR" });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "IsVeg", "Name", "RestaurantId", "price", "currency" },
                values: new object[] { new Guid("b1b2c3d4-0006-4000-8000-000000000006"), "Rice", "Comfort food with tempered curd rice", true, true, "Curd Rice", new Guid("a1b2c3d4-0001-4000-8000-000000000001"), 99m, "INR" });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "Name", "RestaurantId", "price", "currency" },
                values: new object[,]
                {
                    { new Guid("b1b2c3d4-0007-4000-8000-000000000007"), "Burgers", "Double-patty smash burger with house sauce", true, "Classic Smash Burger", new Guid("a1b2c3d4-0002-4000-8000-000000000002"), 299m, "INR" },
                    { new Guid("b1b2c3d4-0008-4000-8000-000000000008"), "Burgers", "Signature burger with truffle mayo and caramelized onions", true, "Truffle Special Burger", new Guid("a1b2c3d4-0002-4000-8000-000000000002"), 449m, "INR" }
                });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "IsVeg", "Name", "RestaurantId", "price", "currency" },
                values: new object[,]
                {
                    { new Guid("b1b2c3d4-0009-4000-8000-000000000009"), "Sides", "Crispy fries with cheese, jalapenos, and sour cream", true, true, "Loaded Fries", new Guid("a1b2c3d4-0002-4000-8000-000000000002"), 199m, "INR" },
                    { new Guid("b1b2c3d4-000a-4000-8000-000000000010"), "Beverages", "Thick chocolate milkshake with whipped cream", true, true, "Chocolate Shake", new Guid("a1b2c3d4-0002-4000-8000-000000000002"), 179m, "INR" }
                });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "Name", "RestaurantId", "price", "currency" },
                values: new object[] { new Guid("b1b2c3d4-000b-4000-8000-000000000011"), "Sandwiches", "Grilled chicken breast with lettuce and garlic aioli", true, "Grilled Chicken Sandwich", new Guid("a1b2c3d4-0002-4000-8000-000000000002"), 279m, "INR" });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "menu_items",
                columns: new[] { "Id", "Category", "Description", "IsAvailable", "IsVeg", "Name", "RestaurantId", "price", "currency" },
                values: new object[,]
                {
                    { new Guid("b1b2c3d4-000c-4000-8000-000000000012"), "Dosa", "Crispy dosa with spiced potato filling", true, true, "Masala Dosa", new Guid("a1b2c3d4-0003-4000-8000-000000000003"), 80m, "INR" },
                    { new Guid("b1b2c3d4-000d-4000-8000-000000000013"), "Dosa", "Butter-roasted dosa, Karnataka specialty", true, true, "Benne Masala Dosa", new Guid("a1b2c3d4-0003-4000-8000-000000000003"), 99m, "INR" },
                    { new Guid("b1b2c3d4-000e-4000-8000-000000000014"), "Breakfast", "Steamed idli with crispy medu vada and sambar", true, true, "Idli Vada", new Guid("a1b2c3d4-0003-4000-8000-000000000003"), 60m, "INR" },
                    { new Guid("b1b2c3d4-000f-4000-8000-000000000015"), "Desserts", "Sweet semolina halwa with ghee and cashews", true, true, "Kesari Bath", new Guid("a1b2c3d4-0003-4000-8000-000000000003"), 50m, "INR" },
                    { new Guid("b1b2c3d4-0010-4000-8000-000000000016"), "Beverages", "South Indian filter coffee, strong and frothy", true, true, "Filter Coffee", new Guid("a1b2c3d4-0003-4000-8000-000000000003"), 30m, "INR" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0001-4000-8000-000000000001"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0002-4000-8000-000000000002"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0003-4000-8000-000000000003"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0004-4000-8000-000000000004"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0005-4000-8000-000000000005"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0006-4000-8000-000000000006"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0007-4000-8000-000000000007"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0008-4000-8000-000000000008"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0009-4000-8000-000000000009"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000a-4000-8000-000000000010"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000b-4000-8000-000000000011"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000c-4000-8000-000000000012"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000d-4000-8000-000000000013"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000e-4000-8000-000000000014"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000f-4000-8000-000000000015"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0010-4000-8000-000000000016"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "restaurants",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0001-4000-8000-000000000001"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "restaurants",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0002-4000-8000-000000000002"));

            migrationBuilder.DeleteData(
                schema: "restaurant",
                table: "restaurants",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0003-4000-8000-000000000003"));
        }
    }
}
