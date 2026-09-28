using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacySecretHashToGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legacy_secret_hash",
                table: "oauth_grants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_tenant_legacy_secret_hash",
                table: "oauth_grants",
                columns: new[] { "tenant_id", "legacy_secret_hash" },
                filter: "\"legacy_secret_hash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oauth_grants_tenant_legacy_secret_hash",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "legacy_secret_hash",
                table: "oauth_grants");
        }
    }
}
