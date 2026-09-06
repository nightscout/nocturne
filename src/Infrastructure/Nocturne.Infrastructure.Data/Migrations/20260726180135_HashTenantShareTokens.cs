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
            // Narrowing to 32 would fail on any stored digest, so clear the column first. The older
            // code compares the column against a plaintext token, so a rollback kills every share
            // link either way; clearing routes the owner to the "public access is off, regenerate"
            // path instead of a link the UI reports as on and nothing can resolve.
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
