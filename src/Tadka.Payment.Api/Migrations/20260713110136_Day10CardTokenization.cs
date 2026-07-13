using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Payment.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day10CardTokenization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CardLast4",
                schema: "payment",
                table: "payments",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardToken",
                schema: "payment",
                table: "payments",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CardLast4",
                schema: "payment",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "CardToken",
                schema: "payment",
                table: "payments");
        }
    }
}
