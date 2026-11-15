using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppBuildLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_build_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    account_login = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    account_is_organization = table.Column<bool>(type: "boolean", nullable: false),
                    repository_full_name = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: true),
                    admin_repositories = table.Column<string[]>(type: "text[]", nullable: false),
                    linked_by_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linker_github_login = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    linker_github_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_build_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_build_links_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_app_build_links_tenant_target",
                table: "app_build_links",
                columns: new[] { "tenant_id", "target_id" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE app_build_links ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE app_build_links FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON app_build_links
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON app_build_links;");
            migrationBuilder.Sql("ALTER TABLE app_build_links NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE app_build_links DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "app_build_links");
        }
    }
}
