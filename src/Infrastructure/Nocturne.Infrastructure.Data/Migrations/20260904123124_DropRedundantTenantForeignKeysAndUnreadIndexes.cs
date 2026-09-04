using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Removes the second, identical tenant foreign key from the tables that carry two, and the
    /// three indexes no query can enter on.
    /// <para>
    /// Dropping a constraint takes an AccessExclusiveLock on its table, so each table is its own
    /// statement and therefore its own transaction, under a short <c>lock_timeout</c>. A table
    /// whose lock cannot be taken is left alone with a notice rather than failing the migration:
    /// migrations run at API startup, so raising here would crash-loop the API over a redundancy
    /// that is harmless to keep.
    /// </para>
    /// <para>
    /// That skip is permanent. The migration still completes, so it takes its
    /// <c>__EFMigrationsHistory</c> row and is never run again; a table skipped here keeps its
    /// duplicate until a later migration removes it. The notice naming the table is the only
    /// record, which is why the migrator data source forwards notices
    /// (<see cref="Extensions.DatabaseInitializationExtensions.BuildMigratorDataSource"/>).
    /// </para>
    /// </summary>
    public partial class DropRedundantTenantForeignKeysAndUnreadIndexes : Migration
    {
        /// <summary>
        /// Tables that carried two identical tenant foreign keys: the snake_case one
        /// <c>20260227034745_EnforceMultitenancy</c> added in a loop, and the EF-conventional one
        /// <c>20260415045216_AddTenantCascadeDeletes</c> added afterwards behind a guard that only
        /// looked for its own name.
        /// </summary>
        private static readonly string[] TablesWithDuplicateTenantForeignKey =
        [
            "aps_snapshots",
            "basal_schedules",
            "bg_checks",
            "bolus_calculations",
            "boluses",
            "calibrations",
            "carb_intakes",
            "carb_ratio_schedules",
            "clock_faces",
            "compression_low_suggestions",
            "connector_configurations",
            "connector_food_entries",
            "data_source_metadata",
            "device_events",
            "devices",
            "discrepancy_analyses",
            "discrepancy_details",
            "foods",
            "heart_rates",
            "in_app_notifications",
            "linked_records",
            "meter_glucose",
            "notes",
            "pump_snapshots",
            "sensitivity_schedules",
            "sensor_glucose",
            "settings",
            "state_spans",
            "step_counts",
            "target_range_schedules",
            "temp_basals",
            "therapy_settings",
            "tracker_definitions",
            "tracker_instances",
            "tracker_notification_thresholds",
            "tracker_presets",
            "treatment_foods",
            "uploader_snapshots",
            "user_food_favorites",
        ];

        /// <summary>
        /// Indexes no statement can enter on. <c>correlation_id</c> on these two tables holds
        /// <c>TraceId</c>, which is written for diagnostics and never filtered; the temp-basal
        /// reads that mention <c>end_timestamp</c> all bound <c>start_timestamp</c> as well and
        /// enter on <c>ix_temp_basals_tenant_start_timestamp</c>.
        /// </summary>
        private static readonly string[] UnreadIndexes =
        [
            "ix_mutation_audit_log_correlation",
            "ix_read_access_log_correlation",
            "ix_temp_basals_end_timestamp",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Matched on shape rather than name, and only where an equivalent constraint survives,
            // so no table can be left without its cascade. A name pattern would miss devices,
            // which carries the stem it had before the rename from pump_devices.
            foreach (var table in TablesWithDuplicateTenantForeignKey)
            {
                migrationBuilder.Sql($"""
                    DO $$
                    DECLARE dup record;
                    BEGIN
                        SET LOCAL lock_timeout = '3s';
                        FOR dup IN
                            SELECT c.conname
                            FROM pg_constraint c
                            WHERE c.contype = 'f'
                              AND c.conrelid = '{table}'::regclass
                              AND c.confrelid = 'tenants'::regclass
                              AND c.conname <> 'FK_{table}_tenants_tenant_id'
                              AND EXISTS (
                                  SELECT 1 FROM pg_constraint keep
                                  WHERE keep.contype = 'f'
                                    AND keep.conrelid = c.conrelid
                                    AND keep.confrelid = c.confrelid
                                    AND keep.conkey = c.conkey
                                    AND keep.confkey = c.confkey
                                    AND keep.confdeltype = c.confdeltype
                                    AND keep.confupdtype = c.confupdtype
                                    AND keep.conname = 'FK_{table}_tenants_tenant_id')
                        LOOP
                            EXECUTE format('ALTER TABLE {table} DROP CONSTRAINT %I', dup.conname);
                            RAISE NOTICE 'dropped redundant % on {table}', dup.conname;
                        END LOOP;
                    EXCEPTION WHEN lock_not_available THEN
                        RAISE NOTICE 'skipped {table}: could not take the lock within 3s';
                    END $$;
                    """, suppressTransaction: true);
            }

            foreach (var name in UnreadIndexes)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, name);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_temp_basals_end_timestamp",
                "ON temp_basals (end_timestamp)");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_read_access_log_correlation",
                "ON read_access_log (correlation_id) WHERE correlation_id IS NOT NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_mutation_audit_log_correlation",
                "ON mutation_audit_log (correlation_id) WHERE correlation_id IS NOT NULL");

            // NOT VALID because the rows were already checked by the surviving constraint that
            // never went away: validating 39 tables again would re-read every one of them under
            // ShareRowExclusive, which is a second outage rather than a rollback. Restores the
            // naming the loop in EnforceMultitenancy used, which for devices predates its rename
            // from pump_devices. A constraint Up removed from a table outside this list was, by
            // the predicate that selected it, redundant with a cascade that is still in place.
            foreach (var table in TablesWithDuplicateTenantForeignKey)
            {
                var name = table == "devices" ? "fk_pump_devices_tenant_id" : $"fk_{table}_tenant_id";

                migrationBuilder.Sql($"""
                    DO $$
                    BEGIN
                        SET LOCAL lock_timeout = '3s';
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '{name}') THEN
                            ALTER TABLE {table} ADD CONSTRAINT {name}
                                FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE NOT VALID;
                        END IF;
                    EXCEPTION WHEN lock_not_available THEN
                        RAISE NOTICE 'skipped {table}: could not take the lock within 3s';
                    END $$;
                    """, suppressTransaction: true);
            }
        }
    }
}
