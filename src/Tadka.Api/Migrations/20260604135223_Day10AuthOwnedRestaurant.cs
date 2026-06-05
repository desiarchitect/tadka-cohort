using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day10AuthOwnedRestaurant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnedRestaurantId",
                schema: "identity",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "identity",
                table: "users",
                keyColumn: "Id",
                keyValue: new Guid("c1b2c3d4-0001-4000-8000-000000000001"),
                column: "OwnedRestaurantId",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnedRestaurantId",
                schema: "identity",
                table: "users");
        }
    }
}
