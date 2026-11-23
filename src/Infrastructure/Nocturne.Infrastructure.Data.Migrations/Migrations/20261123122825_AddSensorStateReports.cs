using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorStateReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "data_source",
                table: "device_status_extras");

            migrationBuilder.DropColumn(
                name: "device",
                table: "device_status_extras");

            migrationBuilder.CreateTable(
                name: "sensor_state_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reported_field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reported_value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    deleted_by_user = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    writer_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensor_state_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_sensor_state_reports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("ALTER TABLE sensor_state_reports ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE sensor_state_reports FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON sensor_state_reports;
                CREATE POLICY tenant_isolation ON sensor_state_reports
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_correlation_id",
                table: "sensor_state_reports",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_sensor_state_reports_tenant_id",
                table: "sensor_state_reports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_tenant_legacy_id",
                table: "sensor_state_reports",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_tenant_legacy_id_user_deleted",
                table: "sensor_state_reports",
                columns: new[] { "tenant_id", "legacy_id" },
                filter: "legacy_id IS NOT NULL AND deleted_by_user");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_tenant_source_device_timestamp",
                table: "sensor_state_reports",
                columns: new[] { "tenant_id", "data_source", "device", "timestamp" },
                descending: new[] { false, false, false, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_tenant_source_timestamp",
                table: "sensor_state_reports",
                columns: new[] { "tenant_id", "data_source", "timestamp" },
                descending: new[] { false, false, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_tenant_timestamp_id",
                table: "sensor_state_reports",
                columns: new[] { "tenant_id", "timestamp", "id" },
                descending: new[] { false, true, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_state_reports_timestamp",
                table: "sensor_state_reports",
                column: "timestamp",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sensor_state_reports");

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "device_status_extras",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "device",
                table: "device_status_extras",
                type: "text",
                nullable: true);
        }
    }
}
