using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceKeyCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "release_notices",
                newName: "owner_notices");

            migrationBuilder.Sql("""
                ALTER TABLE owner_notices RENAME CONSTRAINT "PK_release_notices" TO "PK_owner_notices";
                ALTER TABLE owner_notices RENAME CONSTRAINT "FK_release_notices_tenants_tenant_id" TO "FK_owner_notices_tenants_tenant_id";
                """);

            migrationBuilder.RenameIndex(
                name: "IX_release_notices_tenant_id",
                table: "owner_notices",
                newName: "IX_owner_notices_tenant_id");

            migrationBuilder.RenameIndex(
                name: "ux_release_notices_kind_version_tenant_subject",
                table: "owner_notices",
                newName: "ux_owner_notices_kind_version_tenant_subject");

            migrationBuilder.CreateTable(
                name: "instance_key_check",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ciphertext = table.Column<string>(type: "text", nullable: false),
                    outstanding = table.Column<bool>(type: "boolean", nullable: false),
                    previous_ciphertext = table.Column<string>(type: "text", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_key_check", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instance_key_check");

            migrationBuilder.RenameIndex(
                name: "ux_owner_notices_kind_version_tenant_subject",
                table: "owner_notices",
                newName: "ux_release_notices_kind_version_tenant_subject");

            migrationBuilder.RenameIndex(
                name: "IX_owner_notices_tenant_id",
                table: "owner_notices",
                newName: "IX_release_notices_tenant_id");

            migrationBuilder.Sql("""
                ALTER TABLE owner_notices RENAME CONSTRAINT "PK_owner_notices" TO "PK_release_notices";
                ALTER TABLE owner_notices RENAME CONSTRAINT "FK_owner_notices_tenants_tenant_id" TO "FK_release_notices_tenants_tenant_id";
                """);

            migrationBuilder.RenameTable(
                name: "owner_notices",
                newName: "release_notices");
        }
    }
}
