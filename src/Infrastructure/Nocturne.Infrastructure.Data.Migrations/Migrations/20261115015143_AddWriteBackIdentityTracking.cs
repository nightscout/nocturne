using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// <c>written_live</c> on every legacy-keyed table: whether a live write has touched the row
    /// (<c>IWriteBackTracked</c>). Only such a row can have been written back to Nightscout, so only
    /// its pulled copy is an echo. Existing rows start false: their copies update them as any upstream
    /// record does. A constant default makes each column a catalog-only change.
    /// </summary>
    public partial class AddWriteBackIdentityTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "uploader_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "temp_basals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "sensor_glucose",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "pump_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "profile_activations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "meter_glucose",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "device_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "carb_intakes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "calibrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "boluses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "bolus_calculations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "bg_checks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "aps_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "written_live",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "profile_activations");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "aps_snapshots");
        }
    }
}
