using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the tenant-leading read indexes, all through
    /// <see cref="ConcurrentIndexBuilder"/> — see there for why CONCURRENTLY, and for what an
    /// interrupted build leaves behind.
    /// </summary>
    public partial class AddTenantLeadingReadIndexes : Migration
    {
        /// <summary>
        /// Tables taking the shared <c>(tenant_id, data_source, timestamp DESC)</c> watermark index.
        /// Mirrors <c>NocturneDbContext.V4TimeSeriesRecordEntities</c>.
        /// </summary>
        private static readonly string[] WatermarkedTables =
        [
            "aps_snapshots",
            "basal_injections",
            "basal_schedules",
            "bg_checks",
            "bolus_calculations",
            "boluses",
            "calibrations",
            "carb_intakes",
            "carb_ratio_schedules",
            "device_events",
            "meter_glucose",
            "notes",
            "pump_snapshots",
            "sensitivity_schedules",
            "sensor_glucose",
            "target_range_schedules",
            "therapy_settings",
            "uploader_snapshots",
        ];

        /// <summary>Mirrors <c>NocturneDbContext.V4SnapshotEntities</c>.</summary>
        private static readonly string[] SnapshotTables =
        [
            "aps_snapshots",
            "pump_snapshots",
            "uploader_snapshots",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in WatermarkedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_source_timestamp",
                    $"ON {table} (tenant_id, data_source, \"timestamp\" DESC) WHERE deleted_at IS NULL");
            }

            foreach (var table in SnapshotTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_timestamp",
                    $"ON {table} (tenant_id, \"timestamp\" DESC) WHERE deleted_at IS NULL");
            }

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_linked_records_tenant_created",
                "ON linked_records (tenant_id, sys_created_at)");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_linked_records_tenant_type_timestamp",
                "ON linked_records (tenant_id, record_type, source_timestamp)");

            // Superseded by the tenant-leading index above, which the same reads enter on: the
            // table is reachable only through the tenant query filter and the tenant_isolation RLS
            // policy, so no reader wants a record_type prefix without a tenant. Conditional on the
            // replacement having been built and validated, so an interrupted run cannot leave the
            // window reads with neither index.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    SET LOCAL lock_timeout = '3s';
                    IF EXISTS (
                        SELECT 1 FROM pg_index
                        WHERE indexrelid = to_regclass('public.ix_linked_records_tenant_type_timestamp')
                          AND indisvalid)
                    THEN
                        EXECUTE 'DROP INDEX IF EXISTS public.ix_linked_records_type_timestamp';
                    ELSE
                        RAISE NOTICE
                            'keeping ix_linked_records_type_timestamp: its replacement is absent or invalid';
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_linked_records_type_timestamp",
                "ON linked_records (record_type, source_timestamp)");

            foreach (var name in new[]
            {
                "ix_linked_records_tenant_type_timestamp",
                "ix_linked_records_tenant_created",
            })
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, name);
            }

            foreach (var table in SnapshotTables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_timestamp");
            }

            foreach (var table in WatermarkedTables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_source_timestamp");
            }
        }

    }
}
