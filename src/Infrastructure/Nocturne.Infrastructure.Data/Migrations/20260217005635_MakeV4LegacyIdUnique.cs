using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeV4LegacyIdUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_notes_legacy_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notes_legacy_id",
                table: "notes",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_notes_legacy_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_legacy_id",
                table: "notes",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks",
                column: "legacy_id");
        }
    }
}
