using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropGhostDeliverySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignments",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "agents",
                schema: "delivery");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "delivery");

            migrationBuilder.CreateTable(
                name: "agents",
                schema: "delivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Available"),
                    current_latitude = table.Column<double>(type: "double precision", nullable: true),
                    current_longitude = table.Column<double>(type: "double precision", nullable: true)
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
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PickedUpAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Assigned")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assignments_agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "delivery",
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "delivery",
                table: "agents",
                columns: new[] { "Id", "Name", "Phone", "Status", "current_latitude", "current_longitude" },
                values: new object[,]
                {
                    { new Guid("d1b2c3d4-0001-4000-8000-000000000001"), "Ramesh Kumar", "+919876543210", "Available", 12.9352, 77.624499999999998 },
                    { new Guid("d1b2c3d4-0002-4000-8000-000000000002"), "Suresh Patel", "+919876543211", "Available", 12.978400000000001, 77.640799999999999 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_AgentId",
                schema: "delivery",
                table: "assignments",
                column: "AgentId");
        }
    }
}
