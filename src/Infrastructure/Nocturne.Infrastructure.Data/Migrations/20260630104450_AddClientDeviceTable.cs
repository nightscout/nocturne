using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientDeviceTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    install_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_devices_oauth_grants_grant_id",
                        column: x => x.grant_id,
                        principalTable: "oauth_grants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_client_devices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_devices_grant_id",
                table: "client_devices",
                column: "grant_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_devices_tenant_install",
                table: "client_devices",
                columns: new[] { "tenant_id", "install_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_client_devices_tenant_kind",
                table: "client_devices",
                columns: new[] { "tenant_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_client_devices_tenant_subject",
                table: "client_devices",
                columns: new[] { "tenant_id", "subject_id" });

            // Row Level Security — every tenant-scoped table must ENABLE + FORCE RLS and carry a
            // tenant_isolation policy (USING + WITH CHECK) so both reads and writes are enforced at
            // the database, not just by the EF global query filter. NULLIF keeps the expression safe
            // when the GUC is unset (e.g. migrations under the migrator role).
            migrationBuilder.Sql("ALTER TABLE client_devices ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE client_devices FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON client_devices;
                CREATE POLICY tenant_isolation ON client_devices
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON client_devices;");
            migrationBuilder.DropTable(
                name: "client_devices");
        }
    }
}
