using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day13OutboxTraceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                schema: "ordering",
                table: "outbox_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                schema: "ordering",
                table: "outbox_messages");
        }
    }
}
