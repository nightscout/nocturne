using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipNextReasonLapse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "next_reason_lapse_at",
                table: "tenant_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_next_reason_lapse_at",
                table: "tenant_members",
                column: "next_reason_lapse_at",
                filter: "next_reason_lapse_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_members_next_reason_lapse_at",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "next_reason_lapse_at",
                table: "tenant_members");
        }
    }
}
