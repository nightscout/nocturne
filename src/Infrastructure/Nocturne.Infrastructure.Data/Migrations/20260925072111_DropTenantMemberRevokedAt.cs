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
            // Both indexes lose their revoked_at IS NULL filter, so a revoked row sharing a key with
            // a live one would abort the CREATE UNIQUE INDEX below and crash-loop the API. Removal
            // is a hard delete, so keep the live row (then the newest) and delete the rest; their
            // tenant_member_roles cascade. tenant_members carries no RLS policy, so no tenant GUC.
            migrationBuilder.Sql("""
                DELETE FROM tenant_members
                WHERE id IN (
                    SELECT id
                    FROM (
                        SELECT id,
                               row_number() OVER (
                                   PARTITION BY tenant_id, subject_id
                                   ORDER BY (revoked_at IS NULL) DESC, sys_created_at DESC, id DESC) AS rn
                        FROM tenant_members
                    ) ranked
                    WHERE ranked.rn > 1);

                DELETE FROM tenant_members
                WHERE id IN (
                    SELECT id
                    FROM (
                        SELECT id,
                               row_number() OVER (
                                   PARTITION BY tenant_id, username
                                   ORDER BY (revoked_at IS NULL) DESC, sys_created_at DESC, id DESC) AS rn
                        FROM tenant_members
                        WHERE username IS NOT NULL
                    ) ranked
                    WHERE ranked.rn > 1);
                """);

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
