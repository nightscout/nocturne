using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the device-source index every sourced record table lacked, and the data-source index two of them lacked, through <see cref="ConcurrentIndexBuilder"/>.
    /// </summary>
    public partial class ExtendSourceDeviceIndexToEveryRecordTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_uploader_snapshots_tenant_device_source",
                "ON uploader_snapshots (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_therapy_settings_tenant_device_source",
                "ON therapy_settings (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_target_range_schedules_tenant_device_source",
                "ON target_range_schedules (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_step_counts_tenant_device_source",
                "ON step_counts (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_sensor_state_reports_tenant_device_source",
                "ON sensor_state_reports (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_sensitivity_schedules_tenant_device_source",
                "ON sensitivity_schedules (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_pump_snapshots_tenant_device_source",
                "ON pump_snapshots (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_profile_activations_tenant_device_source",
                "ON profile_activations (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_profile_activations_tenant_source_timestamp",
                "ON profile_activations (tenant_id, data_source, start_timestamp DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_heart_rates_tenant_device_source",
                "ON heart_rates (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_carb_ratio_schedules_tenant_device_source",
                "ON carb_ratio_schedules (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_calibrations_tenant_device_source",
                "ON calibrations (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_body_weights_tenant_device_source",
                "ON body_weights (tenant_id, device, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_body_weights_tenant_source",
                "ON body_weights (tenant_id, data_source) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_basal_schedules_tenant_device_source",
                "ON basal_schedules (tenant_id, device, data_source) WHERE deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_uploader_snapshots_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_therapy_settings_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_target_range_schedules_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_step_counts_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_sensor_state_reports_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_sensitivity_schedules_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_pump_snapshots_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_profile_activations_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_profile_activations_tenant_source_timestamp");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_heart_rates_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_carb_ratio_schedules_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_calibrations_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_body_weights_tenant_device_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_body_weights_tenant_source");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_basal_schedules_tenant_device_source");
        }
    }
}
