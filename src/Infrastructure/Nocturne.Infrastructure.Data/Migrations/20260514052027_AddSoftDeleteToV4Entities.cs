using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDeleteToV4Entities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_therapy_settings_tenant_legacy_id",
                table: "therapy_settings");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_tenant_legacy_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_target_range_schedules_tenant_legacy_id",
                table: "target_range_schedules");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_tenant_legacy_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_tenant_legacy_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_patient_records_tenant_id",
                table: "patient_records");

            migrationBuilder.DropIndex(
                name: "ix_notes_tenant_legacy_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "IX_devices_category_type_serial",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "ix_device_events_tenant_legacy_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_tenant_legacy_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_legacy_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_source_sync_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_legacy_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_source_sync_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_tenant_legacy_id",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_tenant_legacy_id",
                table: "bg_checks");

            migrationBuilder.DropIndex(
                name: "ix_basal_schedules_tenant_legacy_id",
                table: "basal_schedules");

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "uploader_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "therapy_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "temp_basals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "target_range_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "sensor_glucose",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "sensitivity_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "pump_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "patient_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "patient_insulins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "patient_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "meter_glucose",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "device_status_extras",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "device_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "decomposition_batches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "carb_ratio_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "carb_intakes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "calibrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "boluses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "bolus_calculations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "bg_checks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "basal_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "aps_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_tenant_legacy_id",
                table: "therapy_settings",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_tenant_legacy_id",
                table: "temp_basals",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_tenant_legacy_id",
                table: "target_range_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_tenant_legacy_id",
                table: "sensor_glucose",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_tenant_legacy_id",
                table: "sensitivity_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_patient_records_tenant_id",
                table: "patient_records",
                column: "tenant_id",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notes_tenant_legacy_id",
                table: "notes",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_devices_category_type_serial",
                table: "devices",
                columns: new[] { "category", "type", "serial" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_device_events_tenant_legacy_id",
                table: "device_events",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_tenant_legacy_id",
                table: "carb_ratio_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_legacy_id",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_source_sync_id",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_legacy_id",
                table: "boluses",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_source_sync_id",
                table: "boluses",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_tenant_legacy_id",
                table: "bolus_calculations",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_tenant_legacy_id",
                table: "bg_checks",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_tenant_legacy_id",
                table: "basal_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            // Partial indexes for efficient soft-delete cleanup queries
            var tables = new[]
            {
                "aps_snapshots", "basal_schedules", "bg_checks", "bolus_calculations",
                "boluses", "calibrations", "carb_intakes", "carb_ratio_schedules",
                "decomposition_batches", "device_events", "device_status_extras",
                "devices", "meter_glucose", "notes", "patient_devices",
                "patient_insulins", "patient_records", "pump_snapshots",
                "sensitivity_schedules", "sensor_glucose", "target_range_schedules",
                "temp_basals", "therapy_settings", "uploader_snapshots"
            };

            foreach (var table in tables)
            {
                migrationBuilder.CreateIndex(
                    name: $"ix_{table}_deleted_at",
                    table: table,
                    column: "deleted_at",
                    filter: "deleted_at IS NOT NULL");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var tables = new[]
            {
                "aps_snapshots", "basal_schedules", "bg_checks", "bolus_calculations",
                "boluses", "calibrations", "carb_intakes", "carb_ratio_schedules",
                "decomposition_batches", "device_events", "device_status_extras",
                "devices", "meter_glucose", "notes", "patient_devices",
                "patient_insulins", "patient_records", "pump_snapshots",
                "sensitivity_schedules", "sensor_glucose", "target_range_schedules",
                "temp_basals", "therapy_settings", "uploader_snapshots"
            };

            foreach (var table in tables)
            {
                migrationBuilder.DropIndex(
                    name: $"ix_{table}_deleted_at",
                    table: table);
            }

            migrationBuilder.DropIndex(
                name: "ix_therapy_settings_tenant_legacy_id",
                table: "therapy_settings");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_tenant_legacy_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_target_range_schedules_tenant_legacy_id",
                table: "target_range_schedules");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_tenant_legacy_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_tenant_legacy_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_patient_records_tenant_id",
                table: "patient_records");

            migrationBuilder.DropIndex(
                name: "ix_notes_tenant_legacy_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_devices_category_type_serial",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "ix_device_events_tenant_legacy_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_tenant_legacy_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_legacy_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_source_sync_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_legacy_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_source_sync_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_tenant_legacy_id",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_tenant_legacy_id",
                table: "bg_checks");

            migrationBuilder.DropIndex(
                name: "ix_basal_schedules_tenant_legacy_id",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "patient_records");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "patient_devices");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "device_status_extras");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "decomposition_batches");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "aps_snapshots");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_tenant_legacy_id",
                table: "therapy_settings",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_tenant_legacy_id",
                table: "temp_basals",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_tenant_legacy_id",
                table: "target_range_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_tenant_legacy_id",
                table: "sensor_glucose",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_tenant_legacy_id",
                table: "sensitivity_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_patient_records_tenant_id",
                table: "patient_records",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_tenant_legacy_id",
                table: "notes",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_category_type_serial",
                table: "devices",
                columns: new[] { "category", "type", "serial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_events_tenant_legacy_id",
                table: "device_events",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_tenant_legacy_id",
                table: "carb_ratio_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_legacy_id",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_source_sync_id",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_legacy_id",
                table: "boluses",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_source_sync_id",
                table: "boluses",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_tenant_legacy_id",
                table: "bolus_calculations",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_tenant_legacy_id",
                table: "bg_checks",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_tenant_legacy_id",
                table: "basal_schedules",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");
        }
    }
}
