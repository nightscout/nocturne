using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes each V4 list table in its newest-first (timestamp, id) order, so a LIMIT read stops
    /// after n rows. See <c>NocturneDbContext</c> for why. Each snapshot index replaces the
    /// (tenant_id, timestamp) index it extends.
    /// </summary>
    public partial class AddV4ListSortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_sensor_glucose_tenant_timestamp_id",
                "ON sensor_glucose (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_meter_glucose_tenant_timestamp_id",
                "ON meter_glucose (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_calibrations_tenant_timestamp_id",
                "ON calibrations (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_boluses_tenant_timestamp_id",
                "ON boluses (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_carb_intakes_tenant_timestamp_id",
                "ON carb_intakes (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_bg_checks_tenant_timestamp_id",
                "ON bg_checks (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_notes_tenant_timestamp_id",
                "ON notes (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_device_events_tenant_timestamp_id",
                "ON device_events (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_aps_snapshots_tenant_timestamp_id",
                "ON aps_snapshots (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_pump_snapshots_tenant_timestamp_id",
                "ON pump_snapshots (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_uploader_snapshots_tenant_timestamp_id",
                "ON uploader_snapshots (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_state_spans_tenant_start_id",
                "ON state_spans (tenant_id, start_timestamp DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_aps_snapshots_tenant_timestamp");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_pump_snapshots_tenant_timestamp");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_uploader_snapshots_tenant_timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_aps_snapshots_tenant_timestamp",
                "ON aps_snapshots (tenant_id, \"timestamp\" DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_pump_snapshots_tenant_timestamp",
                "ON pump_snapshots (tenant_id, \"timestamp\" DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_uploader_snapshots_tenant_timestamp",
                "ON uploader_snapshots (tenant_id, \"timestamp\" DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_sensor_glucose_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_meter_glucose_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_calibrations_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_boluses_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_carb_intakes_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_bg_checks_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_notes_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_device_events_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_aps_snapshots_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_pump_snapshots_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_uploader_snapshots_tenant_timestamp_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_start_id");
        }
    }
}
