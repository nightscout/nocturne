using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddV4GranularTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bg_checks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    glucose = table.Column<double>(type: "double precision", nullable: false),
                    glucose_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    mgdl = table.Column<double>(type: "double precision", nullable: false),
                    mmol = table.Column<double>(type: "double precision", nullable: true),
                    units = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bg_checks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bolus_calculations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    blood_glucose_input = table.Column<double>(type: "double precision", nullable: true),
                    blood_glucose_input_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    carb_input = table.Column<double>(type: "double precision", nullable: true),
                    insulin_on_board = table.Column<double>(type: "double precision", nullable: true),
                    insulin_recommendation = table.Column<double>(type: "double precision", nullable: true),
                    carb_ratio = table.Column<double>(type: "double precision", nullable: true),
                    calculation_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bolus_calculations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "boluses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    insulin = table.Column<double>(type: "double precision", nullable: false),
                    programmed = table.Column<double>(type: "double precision", nullable: true),
                    delivered = table.Column<double>(type: "double precision", nullable: true),
                    bolus_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    automatic = table.Column<bool>(type: "boolean", nullable: false),
                    duration = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_boluses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "calibrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    slope = table.Column<double>(type: "double precision", nullable: true),
                    intercept = table.Column<double>(type: "double precision", nullable: true),
                    scale = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calibrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "carb_intakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    carbs = table.Column<double>(type: "double precision", nullable: false),
                    protein = table.Column<double>(type: "double precision", nullable: true),
                    fat = table.Column<double>(type: "double precision", nullable: true),
                    food_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    absorption_time = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carb_intakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "meter_glucose",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    mgdl = table.Column<double>(type: "double precision", nullable: false),
                    mmol = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meter_glucose", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    event_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_announcement = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sensor_glucose",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    mgdl = table.Column<double>(type: "double precision", nullable: false),
                    mmol = table.Column<double>(type: "double precision", nullable: true),
                    direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    trend = table.Column<int>(type: "integer", nullable: true),
                    trend_rate = table.Column<double>(type: "double precision", nullable: true),
                    noise = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensor_glucose", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_correlation_id",
                table: "bg_checks",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_mills",
                table: "bg_checks",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_correlation_id",
                table: "bolus_calculations",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_mills",
                table: "bolus_calculations",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_boluses_correlation_id",
                table: "boluses",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_mills",
                table: "boluses",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_correlation_id",
                table: "calibrations",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_legacy_id",
                table: "calibrations",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_mills",
                table: "calibrations",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_correlation_id",
                table: "carb_intakes",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_mills",
                table: "carb_intakes",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_correlation_id",
                table: "meter_glucose",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_legacy_id",
                table: "meter_glucose",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_mills",
                table: "meter_glucose",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_notes_correlation_id",
                table: "notes",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_legacy_id",
                table: "notes",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_mills",
                table: "notes",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_correlation_id",
                table: "sensor_glucose",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_mills",
                table: "sensor_glucose",
                column: "mills",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bg_checks");

            migrationBuilder.DropTable(
                name: "bolus_calculations");

            migrationBuilder.DropTable(
                name: "boluses");

            migrationBuilder.DropTable(
                name: "calibrations");

            migrationBuilder.DropTable(
                name: "carb_intakes");

            migrationBuilder.DropTable(
                name: "meter_glucose");

            migrationBuilder.DropTable(
                name: "notes");

            migrationBuilder.DropTable(
                name: "sensor_glucose");
        }
    }
}
