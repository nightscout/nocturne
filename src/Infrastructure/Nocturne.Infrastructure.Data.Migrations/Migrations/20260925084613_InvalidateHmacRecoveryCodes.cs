using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InvalidateHmacRecoveryCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "invalidated_at",
                table: "recovery_codes",
                type: "timestamp with time zone",
                nullable: true);

            // HMAC-keyed codes cannot be re-hashed, only retired. The rows stay so the recovery
            // status can tell their owner the codes were reset.
            migrationBuilder.Sql(
                "UPDATE recovery_codes SET invalidated_at = now() WHERE invalidated_at IS NULL AND used_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "invalidated_at",
                table: "recovery_codes");
        }
    }
}
