using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HashTenantShareTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "share_token",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing back to 32 fails on any stored digest, which is every non-null row once the
            // startup rotation has run — so clear the column first. That is not data loss beyond
            // what rolling back already causes: the digests are unreadable to the older code, which
            // compares the column against a plaintext token, so every share link is dead either
            // way. Clearing makes the owner's "Public access is off, regenerate to re-enable" path
            // the recovery, instead of leaving a link the UI reports as on and nothing can resolve.
            migrationBuilder.Sql("UPDATE tenants SET share_token = NULL, share_token_set_at = NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "share_token",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
