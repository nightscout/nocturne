using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class V4EntityForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_uploader_snapshots_device_id",
                table: "uploader_snapshots",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_temp_basals_aps_snapshot_id",
                table: "temp_basals",
                column: "aps_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_temp_basals_device_id",
                table: "temp_basals",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_pump_snapshots_device_id",
                table: "pump_snapshots",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_patient_devices_device_id",
                table: "patient_devices",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_boluses_aps_snapshot_id",
                table: "boluses",
                column: "aps_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_boluses_bolus_calculation_id",
                table: "boluses",
                column: "bolus_calculation_id");

            migrationBuilder.CreateIndex(
                name: "IX_boluses_device_id",
                table: "boluses",
                column: "device_id");

            migrationBuilder.AddForeignKey(
                name: "FK_boluses_aps_snapshots_aps_snapshot_id",
                table: "boluses",
                column: "aps_snapshot_id",
                principalTable: "aps_snapshots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_boluses_bolus_calculations_bolus_calculation_id",
                table: "boluses",
                column: "bolus_calculation_id",
                principalTable: "bolus_calculations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_boluses_devices_device_id",
                table: "boluses",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_patient_devices_devices_device_id",
                table: "patient_devices",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_pump_snapshots_devices_device_id",
                table: "pump_snapshots",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_temp_basals_aps_snapshots_aps_snapshot_id",
                table: "temp_basals",
                column: "aps_snapshot_id",
                principalTable: "aps_snapshots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_temp_basals_devices_device_id",
                table: "temp_basals",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_uploader_snapshots_devices_device_id",
                table: "uploader_snapshots",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_boluses_aps_snapshots_aps_snapshot_id",
                table: "boluses");

            migrationBuilder.DropForeignKey(
                name: "FK_boluses_bolus_calculations_bolus_calculation_id",
                table: "boluses");

            migrationBuilder.DropForeignKey(
                name: "FK_boluses_devices_device_id",
                table: "boluses");

            migrationBuilder.DropForeignKey(
                name: "FK_patient_devices_devices_device_id",
                table: "patient_devices");

            migrationBuilder.DropForeignKey(
                name: "FK_pump_snapshots_devices_device_id",
                table: "pump_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_temp_basals_aps_snapshots_aps_snapshot_id",
                table: "temp_basals");

            migrationBuilder.DropForeignKey(
                name: "FK_temp_basals_devices_device_id",
                table: "temp_basals");

            migrationBuilder.DropForeignKey(
                name: "FK_uploader_snapshots_devices_device_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_uploader_snapshots_device_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_temp_basals_aps_snapshot_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "IX_temp_basals_device_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "IX_pump_snapshots_device_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_patient_devices_device_id",
                table: "patient_devices");

            migrationBuilder.DropIndex(
                name: "IX_boluses_aps_snapshot_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "IX_boluses_bolus_calculation_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "IX_boluses_device_id",
                table: "boluses");
        }
    }
}
