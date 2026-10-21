using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindTotpStepUpTokensToHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "admitted_by_invite",
                table: "totp_step_up_tokens",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "bound_tenant_id",
                table: "totp_step_up_tokens",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "admitted_by_invite",
                table: "totp_step_up_tokens");

            migrationBuilder.DropColumn(
                name: "bound_tenant_id",
                table: "totp_step_up_tokens");
        }
    }
}
