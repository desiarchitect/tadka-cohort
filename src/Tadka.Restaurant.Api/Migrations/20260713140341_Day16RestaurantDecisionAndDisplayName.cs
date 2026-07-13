using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Restaurant.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day16RestaurantDecisionAndDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "restaurant",
                table: "menu_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "restaurant",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "order_decisions",
                schema: "restaurant",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_decisions", x => x.OrderId);
                });

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0001-4000-8000-000000000001"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0002-4000-8000-000000000002"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0003-4000-8000-000000000003"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0004-4000-8000-000000000004"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0005-4000-8000-000000000005"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0006-4000-8000-000000000006"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0007-4000-8000-000000000007"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0008-4000-8000-000000000008"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0009-4000-8000-000000000009"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000a-4000-8000-000000000010"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000b-4000-8000-000000000011"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000c-4000-8000-000000000012"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000d-4000-8000-000000000013"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000e-4000-8000-000000000014"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-000f-4000-8000-000000000015"),
                column: "DisplayName",
                value: null);

            migrationBuilder.UpdateData(
                schema: "restaurant",
                table: "menu_items",
                keyColumn: "Id",
                keyValue: new Guid("b1b2c3d4-0010-4000-8000-000000000016"),
                column: "DisplayName",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "restaurant");

            migrationBuilder.DropTable(
                name: "order_decisions",
                schema: "restaurant");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "restaurant",
                table: "menu_items");
        }
    }
}
