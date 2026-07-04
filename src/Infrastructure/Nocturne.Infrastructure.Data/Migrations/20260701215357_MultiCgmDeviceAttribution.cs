using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiCgmDeviceAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rank",
                table: "patient_devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "meter_glucose",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "basal_injections",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_patient_device_id",
                table: "meter_glucose",
                column: "patient_device_id",
                filter: "patient_device_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_basal_injections_patient_device_id",
                table: "basal_injections",
                column: "patient_device_id",
                filter: "patient_device_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_basal_injections_patient_devices_patient_device_id",
                table: "basal_injections",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_meter_glucose_patient_devices_patient_device_id",
                table: "meter_glucose",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_basal_injections_patient_devices_patient_device_id",
                table: "basal_injections");

            migrationBuilder.DropForeignKey(
                name: "FK_meter_glucose_patient_devices_patient_device_id",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "ix_meter_glucose_patient_device_id",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "ix_basal_injections_patient_device_id",
                table: "basal_injections");

            migrationBuilder.DropColumn(
                name: "rank",
                table: "patient_devices");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "basal_injections");
        }
    }
}
