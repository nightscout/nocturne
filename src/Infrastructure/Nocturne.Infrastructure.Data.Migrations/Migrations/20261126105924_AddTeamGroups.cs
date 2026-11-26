using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "staff_reach",
                table: "teams",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "every_person");

            migrationBuilder.CreateTable(
                name: "team_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, computedColumnSql: "lower(name)", stored: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_groups", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_groups_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_group_people",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_group_people", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_group_people_team_groups_team_group_id",
                        column: x => x.team_group_id,
                        principalTable: "team_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_group_people_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_group_staff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_group_staff", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_group_staff_team_groups_team_group_id",
                        column: x => x.team_group_id,
                        principalTable: "team_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_group_staff_team_members_team_member_id",
                        column: x => x.team_member_id,
                        principalTable: "team_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_teams_staff_reach",
                table: "teams",
                sql: "staff_reach IN ('every_person', 'their_groups')");

            migrationBuilder.CreateIndex(
                name: "IX_team_group_people_team_group_id_tenant_id",
                table: "team_group_people",
                columns: new[] { "team_group_id", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_group_people_tenant_id",
                table: "team_group_people",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_group_staff_team_group_id_team_member_id",
                table: "team_group_staff",
                columns: new[] { "team_group_id", "team_member_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_group_staff_team_member_id",
                table: "team_group_staff",
                column: "team_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_groups_team_id_name_key",
                table: "team_groups",
                columns: new[] { "team_id", "name_key" },
                unique: true);

            const string unpinnedSubject = "NULLIF(current_setting('app.current_subject_id', true), '') IS NULL";
            const string subjectPin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";
            const string unpinnedTenant = "NULLIF(current_setting('app.current_tenant_id', true), '') IS NULL";
            const string tenantPin = "NULLIF(current_setting('app.current_tenant_id', true), '')::uuid";

            static string MemberOf(string teamId, string kind) =>
                $"EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = {teamId} AND m.subject_id = {subjectPin}{kind})";
            const string manager = " AND m.kind = 'manager'";
            const string groupTeam = "(SELECT g.team_id FROM team_groups g WHERE g.id = {0}.team_group_id)";

            migrationBuilder.Sql("ALTER TABLE team_groups ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_groups FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY subject_pin ON team_groups
                    USING ({unpinnedSubject} OR {MemberOf("team_groups.team_id", "")})
                    WITH CHECK ({unpinnedSubject} OR {MemberOf("team_groups.team_id", manager)});
                """);

            foreach (var table in new[] { "team_group_people", "team_group_staff" })
            {
                var team = string.Format(groupTeam, table);
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY subject_pin ON {table}
                        USING ({unpinnedSubject} OR {MemberOf(team, "")})
                        WITH CHECK ({unpinnedSubject} OR {MemberOf(team, manager)});
                    """);
            }

            migrationBuilder.Sql($"""
                CREATE POLICY tenant_pin ON team_group_people AS RESTRICTIVE
                    USING ({unpinnedTenant} OR tenant_id = {tenantPin})
                    WITH CHECK ({unpinnedTenant} OR tenant_id = {tenantPin});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_group_people");

            migrationBuilder.DropTable(
                name: "team_group_staff");

            migrationBuilder.DropTable(
                name: "team_groups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_teams_staff_reach",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "staff_reach",
                table: "teams");
        }
    }
}
