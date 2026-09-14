using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropTenantMemberRevokedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members");

            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_username",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "tenant_members");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members",
                columns: new[] { "tenant_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_username",
                table: "tenant_members",
                columns: new[] { "tenant_id", "username" },
                unique: true,
                filter: "username IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members");

            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_username",
                table: "tenant_members");

            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at",
                table: "tenant_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members",
                columns: new[] { "tenant_id", "subject_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_username",
                table: "tenant_members",
                columns: new[] { "tenant_id", "username" },
                unique: true,
                filter: "username IS NOT NULL AND revoked_at IS NULL");
        }
    }
}
