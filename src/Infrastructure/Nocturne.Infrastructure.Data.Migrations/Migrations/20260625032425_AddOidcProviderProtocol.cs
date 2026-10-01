using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcProviderProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "oauth2_settings",
                table: "oidc_providers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_type",
                table: "oidc_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "oidc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "oauth2_settings",
                table: "oidc_providers");

            migrationBuilder.DropColumn(
                name: "provider_type",
                table: "oidc_providers");
        }
    }
}
