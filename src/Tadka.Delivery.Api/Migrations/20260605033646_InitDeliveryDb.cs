using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tadka.Delivery.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitDeliveryDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "delivery");

            migrationBuilder.CreateTable(
                name: "agents",
                schema: "delivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Available")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                schema: "delivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Assigned"),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PickedUpAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "delivery",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => x.MessageId);
                });

            migrationBuilder.InsertData(
                schema: "delivery",
                table: "agents",
                columns: new[] { "Id", "Name", "Phone", "Status" },
                values: new object[,]
                {
                    { new Guid("f0000000-0000-4000-8000-000000000001"), "Suresh", "+919876600001", "Available" },
                    { new Guid("f0000000-0000-4000-8000-000000000002"), "Lakshmi", "+919876600002", "Available" },
                    { new Guid("f0000000-0000-4000-8000-000000000003"), "Imran", "+919876600003", "Available" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_OrderId",
                schema: "delivery",
                table: "assignments",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "assignments",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "delivery");
        }
    }
}
