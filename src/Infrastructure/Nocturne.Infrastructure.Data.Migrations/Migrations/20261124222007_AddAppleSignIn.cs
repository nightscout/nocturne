using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppleSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "provider_refresh_token",
                table: "subject_oidc_identities",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "apple_settings",
                table: "oidc_providers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "provider_refresh_token",
                table: "subject_oidc_identities");

            migrationBuilder.DropColumn(
                name: "apple_settings",
                table: "oidc_providers");
        }
    }
}
