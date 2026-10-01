using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimezoneTimelineAndSensorGlucoseSyncId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "sensor_glucose",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "timezone_timeline",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timezone_timeline", x => x.id);
                    table.ForeignKey(
                        name: "FK_timezone_timeline_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_tenant_source_sync_id",
                table: "sensor_glucose",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_timezone_timeline_tenant_effective_from",
                table: "timezone_timeline",
                columns: new[] { "tenant_id", "effective_from" },
                unique: true);

            // Tenant isolation RLS for the new table (sensor_glucose already has it).
            migrationBuilder.Sql("ALTER TABLE timezone_timeline ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE timezone_timeline FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON timezone_timeline
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON timezone_timeline;");
            migrationBuilder.Sql("ALTER TABLE timezone_timeline NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE timezone_timeline DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "timezone_timeline");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_tenant_source_sync_id",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "sensor_glucose");
        }
    }
}
