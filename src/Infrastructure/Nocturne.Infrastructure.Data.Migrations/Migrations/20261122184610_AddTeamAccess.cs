using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_access_links",
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
                    suggested_role_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    suggested_limit_to_24_hours = table.Column<bool>(type: "boolean", nullable: false),
                    suggested_access_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_access_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_access_links_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_access_links_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_tenant_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    limit_to_24_hours = table.Column<bool>(type: "boolean", nullable: false),
                    access_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_from_link_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_tenant_access", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_tenant_access_subjects_approved_by_subject_id",
                        column: x => x.approved_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_team_tenant_access_team_access_links_created_from_link_id",
                        column: x => x.created_from_link_id,
                        principalTable: "team_access_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_team_tenant_access_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_tenant_access_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_tenant_access_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_tenant_access_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_tenant_access_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_tenant_access_roles_team_tenant_access_team_tenant_acc~",
                        column: x => x.team_tenant_access_id,
                        principalTable: "team_tenant_access",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_tenant_access_roles_tenant_roles_tenant_role_id",
                        column: x => x.tenant_role_id,
                        principalTable: "tenant_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_access_links_created_by_subject_id",
                table: "team_access_links",
                column: "created_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_access_links_team_id",
                table: "team_access_links",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_access_links_token_hash",
                table: "team_access_links",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_tenant_access_approved_by_subject_id",
                table: "team_tenant_access",
                column: "approved_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_tenant_access_created_from_link_id",
                table: "team_tenant_access",
                column: "created_from_link_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_tenant_access_team_id_tenant_id",
                table: "team_tenant_access",
                columns: new[] { "team_id", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_tenant_access_tenant_id",
                table: "team_tenant_access",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_tenant_access_roles_team_tenant_access_id_tenant_role_~",
                table: "team_tenant_access_roles",
                columns: new[] { "team_tenant_access_id", "tenant_role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_tenant_access_roles_tenant_role_id",
                table: "team_tenant_access_roles",
                column: "tenant_role_id");

            const string unpinned = "NULLIF(current_setting('app.current_tenant_id', true), '') IS NULL";
            const string pin = "NULLIF(current_setting('app.current_tenant_id', true), '')::uuid";

            migrationBuilder.Sql("ALTER TABLE team_tenant_access ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_tenant_access FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_pin ON team_tenant_access
                    USING ({unpinned} OR tenant_id = {pin})
                    WITH CHECK ({unpinned} OR tenant_id = {pin});
                """);

            migrationBuilder.Sql("ALTER TABLE team_tenant_access_roles ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_tenant_access_roles FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_pin ON team_tenant_access_roles
                    USING ({unpinned} OR EXISTS (SELECT 1 FROM team_tenant_access a WHERE a.id = team_tenant_access_roles.team_tenant_access_id AND a.tenant_id = {pin}))
                    WITH CHECK ({unpinned} OR EXISTS (SELECT 1 FROM team_tenant_access a WHERE a.id = team_tenant_access_roles.team_tenant_access_id AND a.tenant_id = {pin}));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_tenant_access_roles");

            migrationBuilder.DropTable(
                name: "team_tenant_access");

            migrationBuilder.DropTable(
                name: "team_access_links");
        }
    }
}
