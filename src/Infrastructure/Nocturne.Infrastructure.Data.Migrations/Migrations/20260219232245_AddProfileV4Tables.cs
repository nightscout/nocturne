using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileV4Tables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "basal_schedules",
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
                    profile_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entries_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_basal_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "carb_ratio_schedules",
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
                    profile_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entries_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carb_ratio_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sensitivity_schedules",
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
                    profile_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entries_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitivity_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "target_range_schedules",
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
                    profile_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entries_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_target_range_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "therapy_settings",
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
                    profile_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    units = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    dia = table.Column<double>(type: "double precision", nullable: false),
                    carbs_hr = table.Column<int>(type: "integer", nullable: false),
                    delay = table.Column<int>(type: "integer", nullable: false),
                    per_gi_values = table.Column<bool>(type: "boolean", nullable: true),
                    carbs_hr_high = table.Column<int>(type: "integer", nullable: true),
                    carbs_hr_medium = table.Column<int>(type: "integer", nullable: true),
                    carbs_hr_low = table.Column<int>(type: "integer", nullable: true),
                    delay_high = table.Column<int>(type: "integer", nullable: true),
                    delay_medium = table.Column<int>(type: "integer", nullable: true),
                    delay_low = table.Column<int>(type: "integer", nullable: true),
                    loop_settings_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    entered_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_externally_managed = table.Column<bool>(type: "boolean", nullable: false),
                    start_date = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_therapy_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_correlation_id",
                table: "basal_schedules",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_legacy_id",
                table: "basal_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_mills",
                table: "basal_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_profile_name",
                table: "basal_schedules",
                column: "profile_name");

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_correlation_id",
                table: "carb_ratio_schedules",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_legacy_id",
                table: "carb_ratio_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_mills",
                table: "carb_ratio_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_profile_name",
                table: "carb_ratio_schedules",
                column: "profile_name");

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_correlation_id",
                table: "sensitivity_schedules",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_legacy_id",
                table: "sensitivity_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_mills",
                table: "sensitivity_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_profile_name",
                table: "sensitivity_schedules",
                column: "profile_name");

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_correlation_id",
                table: "target_range_schedules",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_legacy_id",
                table: "target_range_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_mills",
                table: "target_range_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_profile_name",
                table: "target_range_schedules",
                column: "profile_name");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_correlation_id",
                table: "therapy_settings",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_legacy_id",
                table: "therapy_settings",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_mills",
                table: "therapy_settings",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_profile_name",
                table: "therapy_settings",
                column: "profile_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "basal_schedules");

            migrationBuilder.DropTable(
                name: "carb_ratio_schedules");

            migrationBuilder.DropTable(
                name: "sensitivity_schedules");

            migrationBuilder.DropTable(
                name: "target_range_schedules");

            migrationBuilder.DropTable(
                name: "therapy_settings");
        }
    }
}
