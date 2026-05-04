using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorGlucosePatientDeviceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "patient_device_id",
                table: "sensor_glucose",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_patient_device_id",
                table: "sensor_glucose",
                column: "patient_device_id",
                filter: "patient_device_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_sensor_glucose_patient_devices_patient_device_id",
                table: "sensor_glucose",
                column: "patient_device_id",
                principalTable: "patient_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sensor_glucose_patient_devices_patient_device_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_patient_device_id",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "patient_device_id",
                table: "sensor_glucose");
        }
    }
}
