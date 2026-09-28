using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceAndPatientDeviceForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "temp_basals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "pump_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "device_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "device_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "boluses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "aps_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "aps_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_temp_basals_patient_device_id",
                table: "temp_basals",
                column: "patient_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_pump_snapshots_patient_device_id",
                table: "pump_snapshots",
                column: "patient_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_events_device_id",
                table: "device_events",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_events_patient_device_id",
                table: "device_events",
                column: "patient_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_boluses_patient_device_id",
                table: "boluses",
                column: "patient_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_aps_snapshots_device_id",
                table: "aps_snapshots",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_aps_snapshots_patient_device_id",
                table: "aps_snapshots",
                column: "patient_device_id");

            migrationBuilder.AddForeignKey(
                name: "FK_aps_snapshots_devices_device_id",
                table: "aps_snapshots",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_aps_snapshots_patient_devices_patient_device_id",
                table: "aps_snapshots",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_boluses_patient_devices_patient_device_id",
                table: "boluses",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_device_events_devices_device_id",
                table: "device_events",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_device_events_patient_devices_patient_device_id",
                table: "device_events",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_pump_snapshots_patient_devices_patient_device_id",
                table: "pump_snapshots",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_temp_basals_patient_devices_patient_device_id",
                table: "temp_basals",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_aps_snapshots_devices_device_id",
                table: "aps_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_aps_snapshots_patient_devices_patient_device_id",
                table: "aps_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_boluses_patient_devices_patient_device_id",
                table: "boluses");

            migrationBuilder.DropForeignKey(
                name: "FK_device_events_devices_device_id",
                table: "device_events");

            migrationBuilder.DropForeignKey(
                name: "FK_device_events_patient_devices_patient_device_id",
                table: "device_events");

            migrationBuilder.DropForeignKey(
                name: "FK_pump_snapshots_patient_devices_patient_device_id",
                table: "pump_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_temp_basals_patient_devices_patient_device_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "IX_temp_basals_patient_device_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "IX_pump_snapshots_patient_device_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_device_events_device_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "IX_device_events_patient_device_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "IX_boluses_patient_device_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "IX_aps_snapshots_device_id",
                table: "aps_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_aps_snapshots_patient_device_id",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "aps_snapshots");
        }
    }
}
