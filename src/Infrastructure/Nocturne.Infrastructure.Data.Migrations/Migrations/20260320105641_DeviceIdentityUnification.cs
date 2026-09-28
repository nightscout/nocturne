using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceIdentityUnification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the device_health table (no data preservation needed)
            migrationBuilder.DropTable(
                name: "device_health");

            // Rename pump_devices → devices (preserves data)
            migrationBuilder.RenameTable(
                name: "pump_devices",
                newName: "devices");

            // Rename columns on devices table
            migrationBuilder.RenameColumn(
                name: "pump_type",
                table: "devices",
                newName: "type");

            migrationBuilder.RenameColumn(
                name: "pump_serial",
                table: "devices",
                newName: "serial");

            // Add new category column with default for existing rows
            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "InsulinPump");

            // Backfill existing rows
            migrationBuilder.Sql("UPDATE devices SET category = 'InsulinPump' WHERE category IS NULL OR category = '';");

            // Drop old unique index and create new one
            migrationBuilder.DropIndex(
                name: "IX_pump_devices_pump_type_pump_serial",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "IX_devices_category_type_serial",
                table: "devices",
                columns: new[] { "category", "type", "serial" },
                unique: true);

            // Rename pump_device_id → device_id on boluses and temp_basals
            migrationBuilder.RenameColumn(
                name: "pump_device_id",
                table: "temp_basals",
                newName: "device_id");

            migrationBuilder.RenameColumn(
                name: "pump_device_id",
                table: "boluses",
                newName: "device_id");

            // Add device_id columns to related tables
            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "uploader_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "pump_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "patient_devices",
                type: "uuid",
                nullable: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove device_id columns from related tables
            migrationBuilder.DropColumn(
                name: "device_id",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "patient_devices");

            // Rename device_id back to pump_device_id
            migrationBuilder.RenameColumn(
                name: "device_id",
                table: "boluses",
                newName: "pump_device_id");

            migrationBuilder.RenameColumn(
                name: "device_id",
                table: "temp_basals",
                newName: "pump_device_id");

            // Drop new index and restore old one
            migrationBuilder.DropIndex(
                name: "IX_devices_category_type_serial",
                table: "devices");

            // Drop category column
            migrationBuilder.DropColumn(
                name: "category",
                table: "devices");

            // Rename columns back
            migrationBuilder.RenameColumn(
                name: "type",
                table: "devices",
                newName: "pump_type");

            migrationBuilder.RenameColumn(
                name: "serial",
                table: "devices",
                newName: "pump_serial");

            // Rename table back
            migrationBuilder.RenameTable(
                name: "devices",
                newName: "pump_devices");

            migrationBuilder.CreateIndex(
                name: "IX_pump_devices_pump_type_pump_serial",
                table: "pump_devices",
                columns: new[] { "pump_type", "pump_serial" },
                unique: true);

            // Recreate device_health table
            migrationBuilder.CreateTable(
                name: "device_health",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    battery_level = table.Column<decimal>(type: "numeric", nullable: true),
                    battery_warning_threshold = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 20.0m),
                    calibration_reminder_hours = table.Column<int>(type: "integer", nullable: false, defaultValue: 12),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    data_gap_warning_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    device_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    device_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    device_type = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    last_calibration = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_data_received = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_maintenance_alert = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_status_update = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sensor_expiration = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sensor_expiration_warning_hours = table.Column<int>(type: "integer", nullable: false, defaultValue: 24),
                    serial_number = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_health", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_health_created_at",
                table: "device_health",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_device_type",
                table: "device_health",
                column: "device_type");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_last_data_received",
                table: "device_health",
                column: "last_data_received",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_device_health_last_maintenance_alert",
                table: "device_health",
                column: "last_maintenance_alert");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_sensor_expiration",
                table: "device_health",
                column: "sensor_expiration");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_status",
                table: "device_health",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_tenant_device",
                table: "device_health",
                columns: new[] { "tenant_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_health_user_device_type",
                table: "device_health",
                columns: new[] { "user_id", "device_type" });

            migrationBuilder.CreateIndex(
                name: "ix_device_health_user_id",
                table: "device_health",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_user_status",
                table: "device_health",
                columns: new[] { "user_id", "status" });
        }
    }
}
