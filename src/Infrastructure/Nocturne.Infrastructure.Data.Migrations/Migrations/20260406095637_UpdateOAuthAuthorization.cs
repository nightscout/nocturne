using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateOAuthAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants",
                column: "client_id",
                principalTable: "oauth_clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants",
                column: "client_id",
                principalTable: "oauth_clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
