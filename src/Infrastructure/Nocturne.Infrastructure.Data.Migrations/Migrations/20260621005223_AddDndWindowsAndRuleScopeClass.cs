using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDndWindowsAndRuleScopeClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope_class",
                table: "alert_rules",
                type: "text",
                nullable: false,
                defaultValue: "undirected");

            migrationBuilder.CreateTable(
                name: "dnd_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cleared_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnd_windows", x => x.id);
                    table.ForeignKey(
                        name: "FK_dnd_windows_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dnd_windows_tenant_id_scope",
                table: "dnd_windows",
                columns: new[] { "tenant_id", "scope" });

            // Row Level Security — every tenant-scoped table is enrolled (see
            // 20260409020854_AddRlsToNewTenantTables); a suppression audit table is
            // not the place to skip DB-level tenant isolation.
            migrationBuilder.Sql("ALTER TABLE dnd_windows ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE dnd_windows FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON dnd_windows;
                CREATE POLICY tenant_isolation ON dnd_windows
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON dnd_windows;");

            migrationBuilder.DropTable(
                name: "dnd_windows");

            migrationBuilder.DropColumn(
                name: "scope_class",
                table: "alert_rules");
        }
    }
}
