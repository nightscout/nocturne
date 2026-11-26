using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamSiteBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_site_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_site_blocks", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_site_blocks_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_team_site_blocks_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_site_blocks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_site_blocks_created_by_subject_id",
                table: "team_site_blocks",
                column: "created_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_site_blocks_subject_id",
                table: "team_site_blocks",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_site_blocks_tenant_id_subject_id",
                table: "team_site_blocks",
                columns: new[] { "tenant_id", "subject_id" },
                unique: true);

            const string unpinnedTenant = "NULLIF(current_setting('app.current_tenant_id', true), '') IS NULL";
            const string tenantPin = "NULLIF(current_setting('app.current_tenant_id', true), '')::uuid";
            migrationBuilder.Sql("ALTER TABLE team_site_blocks ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_site_blocks FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_pin ON team_site_blocks
                    USING ({unpinnedTenant} OR tenant_id = {tenantPin})
                    WITH CHECK ({unpinnedTenant} OR tenant_id = {tenantPin});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_site_blocks");
        }
    }
}
