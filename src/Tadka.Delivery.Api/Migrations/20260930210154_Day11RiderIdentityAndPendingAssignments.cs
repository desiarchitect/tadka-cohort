using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Delivery.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day11RiderIdentityAndPendingAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                schema: "delivery",
                table: "assignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "delivery",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pending_assignments",
                schema: "delivery",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_assignments", x => x.OrderId);
                });

            migrationBuilder.UpdateData(
                schema: "delivery",
                table: "agents",
                keyColumn: "Id",
                keyValue: new Guid("f0000000-0000-4000-8000-000000000001"),
                column: "UserId",
                value: new Guid("f1000000-0000-4000-8000-000000000001"));

            migrationBuilder.UpdateData(
                schema: "delivery",
                table: "agents",
                keyColumn: "Id",
                keyValue: new Guid("f0000000-0000-4000-8000-000000000002"),
                column: "UserId",
                value: new Guid("f1000000-0000-4000-8000-000000000002"));

            migrationBuilder.UpdateData(
                schema: "delivery",
                table: "agents",
                keyColumn: "Id",
                keyValue: new Guid("f0000000-0000-4000-8000-000000000003"),
                column: "UserId",
                value: new Guid("f1000000-0000-4000-8000-000000000003"));

            migrationBuilder.CreateIndex(
                name: "IX_agents_UserId",
                schema: "delivery",
                table: "agents",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_assignments_CreatedAt",
                schema: "delivery",
                table: "pending_assignments",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_assignments",
                schema: "delivery");

            migrationBuilder.DropIndex(
                name: "IX_agents_UserId",
                schema: "delivery",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                schema: "delivery",
                table: "assignments");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "delivery",
                table: "agents");
        }
    }
}
