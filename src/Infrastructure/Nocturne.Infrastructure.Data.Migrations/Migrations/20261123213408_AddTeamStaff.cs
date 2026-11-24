using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamStaff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "team_manages_staff",
                table: "team_tenant_access",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_from_staff_invite_id",
                table: "team_members",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ends_at",
                table: "team_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "team_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "role_ceiling_slug",
                table: "team_members",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "team_site_staff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_tenant_access_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_site_staff", x => x.id);
                    table.CheckConstraint("ck_team_site_staff_status", "status IN ('pending', 'approved')");
                    table.ForeignKey(
                        name: "FK_team_site_staff_team_members_team_member_id",
                        column: x => x.team_member_id,
                        principalTable: "team_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_site_staff_team_tenant_access_team_tenant_access_id",
                        column: x => x.team_tenant_access_id,
                        principalTable: "team_tenant_access",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_staff_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    role_ceiling_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    limit_to_24_hours = table.Column<bool>(type: "boolean", nullable: false),
                    staff_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_staff_invites", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_staff_invites_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_staff_invites_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_members_created_from_staff_invite_id",
                table: "team_members",
                column: "created_from_staff_invite_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_team_members_kind",
                table: "team_members",
                sql: "kind IN ('manager', 'staff')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_team_members_staff_columns",
                table: "team_members",
                sql: "kind = 'staff' OR (role_ceiling_slug IS NULL AND NOT limit_to_24_hours AND ends_at IS NULL AND created_from_staff_invite_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_team_invites_kind",
                table: "team_invites",
                sql: "kind = 'manager'");

            migrationBuilder.CreateIndex(
                name: "IX_team_site_staff_team_member_id",
                table: "team_site_staff",
                column: "team_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_site_staff_team_tenant_access_id_team_member_id",
                table: "team_site_staff",
                columns: new[] { "team_tenant_access_id", "team_member_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_staff_invites_created_by_subject_id",
                table: "team_staff_invites",
                column: "created_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_staff_invites_team_id",
                table: "team_staff_invites",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_staff_invites_token_hash",
                table: "team_staff_invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_team_members_team_staff_invites_created_from_staff_invite_id",
                table: "team_members",
                column: "created_from_staff_invite_id",
                principalTable: "team_staff_invites",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            const string unpinnedTenant = "NULLIF(current_setting('app.current_tenant_id', true), '') IS NULL";
            const string tenantPin = "NULLIF(current_setting('app.current_tenant_id', true), '')::uuid";
            migrationBuilder.Sql("ALTER TABLE team_site_staff ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_site_staff FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_pin ON team_site_staff
                    USING ({unpinnedTenant} OR EXISTS (SELECT 1 FROM team_tenant_access a WHERE a.id = team_site_staff.team_tenant_access_id AND a.tenant_id = {tenantPin}))
                    WITH CHECK ({unpinnedTenant} OR EXISTS (SELECT 1 FROM team_tenant_access a WHERE a.id = team_site_staff.team_tenant_access_id AND a.tenant_id = {tenantPin}));
                """);

            const string unpinnedSubject = "NULLIF(current_setting('app.current_subject_id', true), '') IS NULL";
            const string subjectPin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";
            migrationBuilder.Sql($"""
                ALTER POLICY subject_pin ON teams
                    WITH CHECK ({unpinnedSubject} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = teams.id AND m.subject_id = {subjectPin} AND m.kind = 'manager'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            const string unpinnedSubject = "NULLIF(current_setting('app.current_subject_id', true), '') IS NULL";
            const string subjectPin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";
            migrationBuilder.Sql($"""
                ALTER POLICY subject_pin ON teams
                    WITH CHECK ({unpinnedSubject} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = teams.id AND m.subject_id = {subjectPin}));
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_team_members_team_staff_invites_created_from_staff_invite_id",
                table: "team_members");

            migrationBuilder.DropTable(
                name: "team_site_staff");

            migrationBuilder.DropTable(
                name: "team_staff_invites");

            migrationBuilder.DropIndex(
                name: "IX_team_members_created_from_staff_invite_id",
                table: "team_members");

            migrationBuilder.DropCheckConstraint(
                name: "ck_team_members_kind",
                table: "team_members");

            migrationBuilder.DropCheckConstraint(
                name: "ck_team_members_staff_columns",
                table: "team_members");

            migrationBuilder.DropCheckConstraint(
                name: "ck_team_invites_kind",
                table: "team_invites");

            migrationBuilder.DropColumn(
                name: "team_manages_staff",
                table: "team_tenant_access");

            migrationBuilder.DropColumn(
                name: "created_from_staff_invite_id",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "ends_at",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "role_ceiling_slug",
                table: "team_members");
        }
    }
}
