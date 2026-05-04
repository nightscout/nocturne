using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestLinkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "activated_at",
                table: "oauth_grants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "activated_ip",
                table: "oauth_grants",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "activated_user_agent",
                table: "oauth_grants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_subject_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "oauth_grants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_grants_created_by_subject_id",
                table: "oauth_grants",
                column: "created_by_subject_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_subjects_created_by_subject_id",
                table: "oauth_grants",
                column: "created_by_subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_subjects_created_by_subject_id",
                table: "oauth_grants");

            migrationBuilder.DropIndex(
                name: "IX_oauth_grants_created_by_subject_id",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "activated_at",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "activated_ip",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "activated_user_agent",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "created_by_subject_id",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "oauth_grants");
        }
    }
}
