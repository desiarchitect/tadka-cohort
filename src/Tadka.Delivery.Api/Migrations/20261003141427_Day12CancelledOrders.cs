using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Delivery.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day12CancelledOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cancelled_orders",
                schema: "delivery",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cancelled_orders", x => x.OrderId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cancelled_orders",
                schema: "delivery");
        }
    }
}
