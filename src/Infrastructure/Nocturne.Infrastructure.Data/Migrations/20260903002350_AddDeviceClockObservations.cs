using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceClockObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_clock_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    observed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_estimate = table.Column<bool>(type: "boolean", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    covers_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    declared_timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_clock_observations", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_clock_observations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_clock_observations_tenant_connector_source_observed",
                table: "device_clock_observations",
                columns: new[] { "tenant_id", "connector", "source", "observed_at" },
                unique: true);

            // Tenant isolation RLS for the new table.
            migrationBuilder.Sql("ALTER TABLE device_clock_observations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE device_clock_observations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON device_clock_observations
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON device_clock_observations;");
            migrationBuilder.Sql("ALTER TABLE device_clock_observations NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE device_clock_observations DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "device_clock_observations");
        }
    }
}
