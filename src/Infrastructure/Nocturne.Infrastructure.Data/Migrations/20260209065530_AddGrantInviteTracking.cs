using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGrantInviteTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "created_from_invite_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_grants_created_from_invite_id",
                table: "oauth_grants",
                column: "created_from_invite_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_follower_invites_created_from_invite_id",
                table: "oauth_grants",
                column: "created_from_invite_id",
                principalTable: "follower_invites",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_follower_invites_created_from_invite_id",
                table: "oauth_grants");

            migrationBuilder.DropIndex(
                name: "IX_oauth_grants_created_from_invite_id",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "created_from_invite_id",
                table: "oauth_grants");
        }
    }
}
