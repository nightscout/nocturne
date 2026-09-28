using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdditionalProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "uploader_snapshots",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "therapy_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "temp_basals",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "target_range_schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "sensor_glucose",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "sensitivity_schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "pump_snapshots",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "pump_devices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "notes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "meter_glucose",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "device_events",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "carb_ratio_schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "carb_intakes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "calibrations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "boluses",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "bolus_calculations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "bg_checks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "basal_schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "aps_snapshots",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "pump_devices");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "additional_properties",
                table: "aps_snapshots");
        }
    }
}
