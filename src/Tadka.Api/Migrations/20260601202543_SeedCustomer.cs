using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeedCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "identity",
                table: "users",
                columns: new[] { "Id", "CreatedAt", "Email", "Name", "PasswordHash", "Phone" },
                values: new object[] { new Guid("c1b2c3d4-0001-4000-8000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "priya@tadka.test", "Priya Sharma", "seed-not-a-real-hash", "+919876500001" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "users",
                keyColumn: "Id",
                keyValue: new Guid("c1b2c3d4-0001-4000-8000-000000000001"));
        }
    }
}
