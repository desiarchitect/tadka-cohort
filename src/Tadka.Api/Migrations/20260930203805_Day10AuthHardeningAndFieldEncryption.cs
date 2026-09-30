using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tadka.Api.Migrations
{
    /// <inheritdoc />
    public partial class Day10AuthHardeningAndFieldEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "users",
                keyColumn: "Id",
                keyValue: new Guid("c1b2c3d4-0001-4000-8000-000000000001"));

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "identity",
                table: "users",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(15)",
                oldMaxLength: 15);

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntil",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_FamilyId",
                schema: "identity",
                table: "refresh_tokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                schema: "identity",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                schema: "identity",
                table: "refresh_tokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                schema: "identity",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "identity",
                table: "users",
                type: "character varying(15)",
                maxLength: 15,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);

            migrationBuilder.InsertData(
                schema: "identity",
                table: "users",
                columns: new[] { "Id", "CreatedAt", "Email", "Name", "OwnedRestaurantId", "PasswordHash", "Phone", "Role" },
                values: new object[] { new Guid("c1b2c3d4-0001-4000-8000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "priya@tadka.test", "Priya Sharma", null, "seed-not-a-real-hash", "+919876500001", "Customer" });
        }
    }
}
