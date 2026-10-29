using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthResourceIndicator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "resource",
                table: "oauth_grants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resource",
                table: "oauth_authorization_codes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resource",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "resource",
                table: "oauth_authorization_codes");
        }
    }
}
