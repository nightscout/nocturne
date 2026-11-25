using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamSiteStaffDecline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_team_site_staff_status",
                table: "team_site_staff");

            migrationBuilder.CreateTable(
                name: "team_site_declines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_tenant_access_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_site_declines", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_site_declines_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_site_declines_team_tenant_access_team_tenant_access_id",
                        column: x => x.team_tenant_access_id,
                        principalTable: "team_tenant_access",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_team_site_staff_status",
                table: "team_site_staff",
                sql: "status IN ('pending', 'approved', 'declined')");

            migrationBuilder.CreateIndex(
                name: "IX_team_site_declines_subject_id",
                table: "team_site_declines",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_site_declines_team_tenant_access_id_subject_id",
                table: "team_site_declines",
                columns: new[] { "team_tenant_access_id", "subject_id" },
                unique: true);

            const string unpinnedTenant = "NULLIF(current_setting('app.current_tenant_id', true), '') IS NULL";
            const string tenantPin = "NULLIF(current_setting('app.current_tenant_id', true), '')::uuid";
            migrationBuilder.Sql("ALTER TABLE team_site_declines ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_site_declines FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_pin ON team_site_declines
                    USING ({unpinnedTenant} OR EXISTS (SELECT 1 FROM team_tenant_access a WHERE a.id = team_site_declines.team_tenant_access_id AND a.tenant_id = {tenantPin}))
                    WITH CHECK ({unpinnedTenant} OR EXISTS (SELECT 1 FROM team_tenant_access a WHERE a.id = team_site_declines.team_tenant_access_id AND a.tenant_id = {tenantPin}));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_site_declines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_team_site_staff_status",
                table: "team_site_staff");

            migrationBuilder.AddCheckConstraint(
                name: "ck_team_site_staff_status",
                table: "team_site_staff",
                sql: "status IN ('pending', 'approved')");
        }
    }
}
