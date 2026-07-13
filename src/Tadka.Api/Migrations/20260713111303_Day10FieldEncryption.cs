using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day10FieldEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "users",
                keyColumn: "Id",
                keyValue: new Guid("c1b2c3d4-0001-4000-8000-000000000001"));

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "identity",
                table: "users",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(15)",
                oldMaxLength: 15);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "identity",
                table: "users",
                type: "character varying(15)",
                maxLength: 15,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);

            migrationBuilder.InsertData(
                schema: "identity",
                table: "users",
                columns: new[] { "Id", "CreatedAt", "Email", "Name", "OwnedRestaurantId", "PasswordHash", "Phone", "Role" },
                values: new object[] { new Guid("c1b2c3d4-0001-4000-8000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "priya@tadka.test", "Priya Sharma", null, "seed-not-a-real-hash", "+919876500001", "Customer" });
        }
    }
}
