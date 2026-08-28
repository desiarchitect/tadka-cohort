using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day04Hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: 'xmin' is a PostgreSQL system column present on every table — it is NOT created
            // here. We only map it as an optimistic-concurrency token in OrderConfiguration (ADR-012);
            // EF's scaffolder emitted an AddColumn for it, which we intentionally removed because the
            // column already exists. Only the idempotency_keys table is a real schema change.
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "ordering",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "ordering");
            // No DropColumn for 'xmin' — it is a system column we never created.
        }
    }
}
