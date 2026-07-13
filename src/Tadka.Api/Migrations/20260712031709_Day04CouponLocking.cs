using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day04CouponLocking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coupon_redemptions",
                schema: "ordering",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CouponId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedeemedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupon_redemptions", x => x.Id);
                });

            // NOTE: 'xmin' is a PostgreSQL system column present on every table automatically —
            // it is NOT declared here. We only map it as an optimistic-concurrency token in
            // CouponConfiguration (same convention as Orders, ADR-012). EF's scaffolder emitted a
            // column for it, which we removed: declaring it would collide with the system column
            // Postgres already creates for free.
            migrationBuilder.CreateTable(
                name: "coupons",
                schema: "ordering",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: false),
                    Redeemed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupons", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "ordering",
                table: "coupons",
                columns: new[] { "Id", "Code", "MaxRedemptions", "Redeemed" },
                values: new object[] { new Guid("c0000000-0001-4000-8000-000000000001"), "TADKA50", 100, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_coupon_redemptions_CouponId",
                schema: "ordering",
                table: "coupon_redemptions",
                column: "CouponId");

            migrationBuilder.CreateIndex(
                name: "IX_coupons_Code",
                schema: "ordering",
                table: "coupons",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coupon_redemptions",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "coupons",
                schema: "ordering");
        }
    }
}
