using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeedDeliveryAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "delivery",
                table: "agents",
                columns: new[] { "Id", "Name", "Phone", "Status", "current_latitude", "current_longitude" },
                values: new object[,]
                {
                    { new Guid("d1b2c3d4-0001-4000-8000-000000000001"), "Ramesh Kumar", "+919876543210", "Available", 12.9352, 77.624499999999998 },
                    { new Guid("d1b2c3d4-0002-4000-8000-000000000002"), "Suresh Patel", "+919876543211", "Available", 12.978400000000001, 77.640799999999999 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "delivery",
                table: "agents",
                keyColumn: "Id",
                keyValue: new Guid("d1b2c3d4-0001-4000-8000-000000000001"));

            migrationBuilder.DeleteData(
                schema: "delivery",
                table: "agents",
                keyColumn: "Id",
                keyValue: new Guid("d1b2c3d4-0002-4000-8000-000000000002"));
        }
    }
}
