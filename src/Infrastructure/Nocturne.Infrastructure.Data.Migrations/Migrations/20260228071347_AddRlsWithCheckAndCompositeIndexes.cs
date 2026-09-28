using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRlsWithCheckAndCompositeIndexes : Migration
    {
        /// <summary>
        /// All tenant-scoped tables that have existing USING-only RLS policies
        /// from the EnforceMultitenancy migration.
        /// </summary>
        private static readonly string[] TenantScopedTables =
        [
            "entries", "treatments", "devicestatus", "foods",
            "connector_food_entries", "treatment_foods", "user_food_favorites",
            "settings", "profiles", "activities", "step_counts", "heart_rates",
            "discrepancy_analyses", "discrepancy_details",
            "alert_rules", "alert_history",
            "notification_preferences", "emergency_contacts", "device_health",
            "data_source_metadata",
            "tracker_definitions", "tracker_instances", "tracker_presets",
            "tracker_notification_thresholds",
            "state_spans", "linked_records", "connector_configurations",
            "in_app_notifications", "clock_faces", "compression_low_suggestions",
            // V4 tables
            "sensor_glucose", "meter_glucose", "calibrations",
            "boluses", "carb_intakes", "bg_checks", "notes", "device_events",
            "bolus_calculations", "aps_snapshots", "pump_snapshots",
            "uploader_snapshots", "pump_devices", "temp_basals",
            "therapy_settings", "basal_schedules", "carb_ratio_schedules",
            "sensitivity_schedules", "target_range_schedules",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop existing USING-only RLS policies and recreate with both
            // USING (read enforcement) and WITH CHECK (write enforcement) clauses.
            // Use missing_ok := true + NULLIF so the policy expression is safe
            // to evaluate when the GUC is unset (e.g. during EF migrations
            // running under the migrator role). An unset GUC collapses to
            // NULL, tenant_id = NULL is NULL, treated as false — rows
            // excluded from SELECT/UPDATE/DELETE and INSERTs rejected. This
            // matters for later schema migrations that add self-referencing
            // FKs (PostgreSQL's internal FK validation query runs through
            // the policy and would otherwise fail on ''::uuid).
            foreach (var table in TenantScopedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                    """);
            }

            migrationBuilder.DropIndex(
                name: "ix_user_food_favorites_user_food",
                table: "user_food_favorites");

            migrationBuilder.DropIndex(
                name: "ix_therapy_settings_legacy_id",
                table: "therapy_settings");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_legacy_id",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_target_range_schedules_legacy_id",
                table: "target_range_schedules");

            migrationBuilder.DropIndex(
                name: "ix_settings_key",
                table: "settings");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_legacy_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_notification_preferences_user_id",
                table: "notification_preferences");

            migrationBuilder.DropIndex(
                name: "ix_notes_legacy_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_linked_records_unique",
                table: "linked_records");

            migrationBuilder.DropIndex(
                name: "ix_foods_external_source_id",
                table: "foods");

            migrationBuilder.DropIndex(
                name: "ix_device_health_device_id",
                table: "device_health");

            migrationBuilder.DropIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_data_source_metadata_device_id",
                table: "data_source_metadata");

            migrationBuilder.DropIndex(
                name: "ix_connector_food_entries_source_entry",
                table: "connector_food_entries");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_legacy_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks");

            migrationBuilder.DropIndex(
                name: "ix_basal_schedules_legacy_id",
                table: "basal_schedules");

            migrationBuilder.CreateIndex(
                name: "ix_user_food_favorites_tenant_user_food",
                table: "user_food_favorites",
                columns: new[] { "tenant_id", "user_id", "food_id" },
                unique: true);

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
                name: "ix_settings_tenant_id_key",
                table: "settings",
                columns: new[] { "tenant_id", "key" },
                unique: true);

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
                name: "ix_notification_preferences_tenant_user",
                table: "notification_preferences",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_tenant_legacy_id",
                table: "notes",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_record",
                table: "linked_records",
                columns: new[] { "record_type", "record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_tenant_type_id",
                table: "linked_records",
                columns: new[] { "tenant_id", "record_type", "record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_foods_tenant_external",
                table: "foods",
                columns: new[] { "tenant_id", "external_source", "external_id" },
                unique: true,
                filter: "external_source IS NOT NULL AND external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_tenant_device",
                table: "device_health",
                columns: new[] { "tenant_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_events_tenant_legacy_id",
                table: "device_events",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_data_source_metadata_tenant_device",
                table: "data_source_metadata",
                columns: new[] { "tenant_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_tenant_source_id",
                table: "connector_food_entries",
                columns: new[] { "tenant_id", "connector_source", "external_entry_id" },
                unique: true);

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
                name: "ix_boluses_tenant_legacy_id",
                table: "boluses",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to USING-only policies (read enforcement only). Keep
            // the NULLIF + missing_ok form so the Down path is also safe to
            // run under the migrator without a tenant context set.
            foreach (var table in TenantScopedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                    """);
            }

            migrationBuilder.DropIndex(
                name: "ix_user_food_favorites_tenant_user_food",
                table: "user_food_favorites");

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
                name: "ix_settings_tenant_id_key",
                table: "settings");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_tenant_legacy_id",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_tenant_legacy_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_notification_preferences_tenant_user",
                table: "notification_preferences");

            migrationBuilder.DropIndex(
                name: "ix_notes_tenant_legacy_id",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_linked_records_record",
                table: "linked_records");

            migrationBuilder.DropIndex(
                name: "ix_linked_records_tenant_type_id",
                table: "linked_records");

            migrationBuilder.DropIndex(
                name: "ix_foods_tenant_external",
                table: "foods");

            migrationBuilder.DropIndex(
                name: "ix_device_health_tenant_device",
                table: "device_health");

            migrationBuilder.DropIndex(
                name: "ix_device_events_tenant_legacy_id",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_data_source_metadata_tenant_device",
                table: "data_source_metadata");

            migrationBuilder.DropIndex(
                name: "ix_connector_food_entries_tenant_source_id",
                table: "connector_food_entries");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_tenant_legacy_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_legacy_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_legacy_id",
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

            migrationBuilder.CreateIndex(
                name: "ix_user_food_favorites_user_food",
                table: "user_food_favorites",
                columns: new[] { "user_id", "food_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_legacy_id",
                table: "therapy_settings",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_legacy_id",
                table: "temp_basals",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_legacy_id",
                table: "target_range_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_settings_key",
                table: "settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_legacy_id",
                table: "sensor_glucose",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_legacy_id",
                table: "sensitivity_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_user_id",
                table: "notification_preferences",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notes_legacy_id",
                table: "notes",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_unique",
                table: "linked_records",
                columns: new[] { "record_type", "record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_foods_external_source_id",
                table: "foods",
                columns: new[] { "external_source", "external_id" },
                unique: true,
                filter: "external_source IS NOT NULL AND external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_device_health_device_id",
                table: "device_health",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_data_source_metadata_device_id",
                table: "data_source_metadata",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_source_entry",
                table: "connector_food_entries",
                columns: new[] { "connector_source", "external_entry_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_legacy_id",
                table: "carb_ratio_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_legacy_id",
                table: "carb_intakes",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_legacy_id",
                table: "boluses",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_legacy_id",
                table: "bolus_calculations",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_legacy_id",
                table: "bg_checks",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_legacy_id",
                table: "basal_schedules",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");
        }
    }
}
