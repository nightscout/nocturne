using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRbac : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create new tables first (before dropping old columns)
            migrationBuilder.CreateTable(
                name: "tenant_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    permissions = table.Column<string>(type: "jsonb", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_roles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_member_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_member_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_member_roles_tenant_members_tenant_member_id",
                        column: x => x.tenant_member_id,
                        principalTable: "tenant_members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tenant_member_roles_tenant_roles_tenant_role_id",
                        column: x => x.tenant_role_id,
                        principalTable: "tenant_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_member_roles_tenant_member_id_tenant_role_id",
                table: "tenant_member_roles",
                columns: new[] { "tenant_member_id", "tenant_role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_member_roles_tenant_role_id",
                table: "tenant_member_roles",
                column: "tenant_role_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_roles_tenant_id_slug",
                table: "tenant_roles",
                columns: new[] { "tenant_id", "slug" },
                unique: true);

            // 2. Add role_ids column to member_invites
            migrationBuilder.AddColumn<string>(
                name: "role_ids",
                table: "member_invites",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // 3. Data migration: seed default roles per existing tenant
            migrationBuilder.Sql("""
                INSERT INTO tenant_roles (id, tenant_id, name, slug, description, permissions, is_system, sys_created_at, sys_updated_at)
                SELECT gen_random_uuid(), t."Id", 'Owner', 'owner', NULL, '["*"]'::jsonb, true, now(), now()
                FROM tenants t
                WHERE NOT EXISTS (SELECT 1 FROM tenant_roles tr WHERE tr.tenant_id = t."Id" AND tr.slug = 'owner');

                INSERT INTO tenant_roles (id, tenant_id, name, slug, description, permissions, is_system, sys_created_at, sys_updated_at)
                SELECT gen_random_uuid(), t."Id", 'Administrator', 'admin', NULL,
                    '["entries.readwrite","treatments.readwrite","devicestatus.readwrite","profile.readwrite","notifications.readwrite","reports.read","health.read","identity.read","members.invite","members.manage","tenant.settings","roles.manage","sharing.manage"]'::jsonb,
                    true, now(), now()
                FROM tenants t
                WHERE NOT EXISTS (SELECT 1 FROM tenant_roles tr WHERE tr.tenant_id = t."Id" AND tr.slug = 'admin');

                INSERT INTO tenant_roles (id, tenant_id, name, slug, description, permissions, is_system, sys_created_at, sys_updated_at)
                SELECT gen_random_uuid(), t."Id", 'Caretaker', 'caretaker', NULL,
                    '["entries.read","treatments.readwrite","devicestatus.read","profile.read","notifications.read","reports.read","health.read"]'::jsonb,
                    true, now(), now()
                FROM tenants t
                WHERE NOT EXISTS (SELECT 1 FROM tenant_roles tr WHERE tr.tenant_id = t."Id" AND tr.slug = 'caretaker');

                INSERT INTO tenant_roles (id, tenant_id, name, slug, description, permissions, is_system, sys_created_at, sys_updated_at)
                SELECT gen_random_uuid(), t."Id", 'Follower', 'follower', NULL,
                    '["entries.read","health.read"]'::jsonb,
                    true, now(), now()
                FROM tenants t
                WHERE NOT EXISTS (SELECT 1 FROM tenant_roles tr WHERE tr.tenant_id = t."Id" AND tr.slug = 'follower');
                """);

            // 4. Map existing member role strings to tenant_member_roles join table
            migrationBuilder.Sql("""
                INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id, sys_created_at)
                SELECT gen_random_uuid(), tm."Id", tr.id, now()
                FROM tenant_members tm
                JOIN tenant_roles tr ON tr.tenant_id = tm.tenant_id AND tr.slug = tm.role
                WHERE tm.revoked_at IS NULL;
                """);

            // 5. Map existing invite role strings to role_ids array
            migrationBuilder.Sql("""
                UPDATE member_invites mi
                SET role_ids = (
                    SELECT COALESCE(jsonb_agg(tr.id), '[]'::jsonb)
                    FROM tenant_roles tr
                    WHERE tr.tenant_id = mi.tenant_id AND tr.slug = mi.role
                )
                WHERE mi.role IS NOT NULL;
                """);

            // 6. Now safe to drop old role columns
            migrationBuilder.DropColumn(
                name: "role",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "role",
                table: "member_invites");

            // 7. Rename scopes to direct_permissions
            migrationBuilder.RenameColumn(
                name: "scopes",
                table: "tenant_members",
                newName: "direct_permissions");

            migrationBuilder.RenameColumn(
                name: "scopes",
                table: "member_invites",
                newName: "direct_permissions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore column names
            migrationBuilder.RenameColumn(
                name: "direct_permissions",
                table: "tenant_members",
                newName: "scopes");

            migrationBuilder.RenameColumn(
                name: "direct_permissions",
                table: "member_invites",
                newName: "scopes");

            // Re-add role columns
            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "tenant_members",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "follower");

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "member_invites",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Restore role strings from join table
            migrationBuilder.Sql("""
                UPDATE tenant_members tm
                SET role = COALESCE((
                    SELECT tr.slug
                    FROM tenant_member_roles tmr
                    JOIN tenant_roles tr ON tr.id = tmr.tenant_role_id
                    WHERE tmr.tenant_member_id = tm."Id"
                    ORDER BY tr.is_system DESC
                    LIMIT 1
                ), 'follower');
                """);

            // Drop new tables and columns
            migrationBuilder.DropTable(name: "tenant_member_roles");
            migrationBuilder.DropTable(name: "tenant_roles");
            migrationBuilder.DropColumn(name: "role_ids", table: "member_invites");
        }
    }
}
