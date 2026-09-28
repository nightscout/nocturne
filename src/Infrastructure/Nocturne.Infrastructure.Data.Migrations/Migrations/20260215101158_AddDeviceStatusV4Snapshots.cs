using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceStatusV4Snapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aps_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    aps_system = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    iob = table.Column<double>(type: "double precision", nullable: true),
                    basal_iob = table.Column<double>(type: "double precision", nullable: true),
                    bolus_iob = table.Column<double>(type: "double precision", nullable: true),
                    cob = table.Column<double>(type: "double precision", nullable: true),
                    current_bg = table.Column<double>(type: "double precision", nullable: true),
                    eventual_bg = table.Column<double>(type: "double precision", nullable: true),
                    target_bg = table.Column<double>(type: "double precision", nullable: true),
                    recommended_bolus = table.Column<double>(type: "double precision", nullable: true),
                    sensitivity_ratio = table.Column<double>(type: "double precision", nullable: true),
                    enacted = table.Column<bool>(type: "boolean", nullable: false),
                    enacted_rate = table.Column<double>(type: "double precision", nullable: true),
                    enacted_duration = table.Column<int>(type: "integer", nullable: true),
                    enacted_bolus_volume = table.Column<double>(type: "double precision", nullable: true),
                    suggested_json = table.Column<string>(type: "jsonb", nullable: true),
                    enacted_json = table.Column<string>(type: "jsonb", nullable: true),
                    predicted_default_json = table.Column<string>(type: "jsonb", nullable: true),
                    predicted_iob_json = table.Column<string>(type: "jsonb", nullable: true),
                    predicted_zt_json = table.Column<string>(type: "jsonb", nullable: true),
                    predicted_cob_json = table.Column<string>(type: "jsonb", nullable: true),
                    predicted_uam_json = table.Column<string>(type: "jsonb", nullable: true),
                    predicted_start_mills = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aps_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pump_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reservoir = table.Column<double>(type: "double precision", nullable: true),
                    reservoir_display = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    battery_percent = table.Column<int>(type: "integer", nullable: true),
                    battery_voltage = table.Column<double>(type: "double precision", nullable: true),
                    bolusing = table.Column<bool>(type: "boolean", nullable: true),
                    suspended = table.Column<bool>(type: "boolean", nullable: true),
                    pump_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    clock = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pump_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "uploader_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    battery = table.Column<int>(type: "integer", nullable: true),
                    battery_voltage = table.Column<double>(type: "double precision", nullable: true),
                    is_charging = table.Column<bool>(type: "boolean", nullable: true),
                    temperature = table.Column<double>(type: "double precision", nullable: true),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uploader_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_legacy_id",
                table: "aps_snapshots",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_mills",
                table: "aps_snapshots",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_legacy_id",
                table: "pump_snapshots",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_mills",
                table: "pump_snapshots",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_legacy_id",
                table: "uploader_snapshots",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_mills",
                table: "uploader_snapshots",
                column: "mills",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aps_snapshots");

            migrationBuilder.DropTable(
                name: "pump_snapshots");

            migrationBuilder.DropTable(
                name: "uploader_snapshots");
        }
    }
}
