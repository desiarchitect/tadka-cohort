using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tadka.Restaurant.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitRestaurantDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "restaurant");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "restaurant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "restaurants",
                schema: "restaurant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address_line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address_city = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    address_pincode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AvgPrepTimeMinutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restaurants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "menu_items",
                schema: "restaurant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "INR"),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsVeg = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RestaurantId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_menu_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_menu_items_restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalSchema: "restaurant",
                        principalTable: "restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "restaurant",
                table: "restaurants",
                columns: new[] { "Id", "address_city", "latitude", "address_line1", "address_line2", "longitude", "address_pincode", "AvgPrepTimeMinutes", "CreatedAt", "IsActive", "Name" },
                values: new object[,]
                {
                    { new Guid("a1b2c3d4-0001-4000-8000-000000000001"), "Bangalore", 12.9352, "124, Near Forum Mall", "Koramangala 5th Block", 77.624499999999998, "560095", 25, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Meghana Foods" },
                    { new Guid("a1b2c3d4-0002-4000-8000-000000000002"), "Bangalore", 12.978400000000001, "96, 12th Main Road", "HAL 2nd Stage, Indiranagar", 77.640799999999999, "560038", 30, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Truffles" },
                    { new Guid("a1b2c3d4-0003-4000-8000-000000000003"), "Bangalore", 12.945399999999999, "32, Gandhi Bazaar Main Road", "Basavanagudi", 77.572599999999994, "560004", 20, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Vidyarthi Bhavan" }
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

            migrationBuilder.CreateIndex(
                name: "IX_menu_items_RestaurantId",
                schema: "restaurant",
                table: "menu_items",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt",
                schema: "restaurant",
                table: "outbox_messages",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "menu_items",
                schema: "restaurant");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "restaurant");

            migrationBuilder.DropTable(
                name: "restaurants",
                schema: "restaurant");
        }
    }
}
