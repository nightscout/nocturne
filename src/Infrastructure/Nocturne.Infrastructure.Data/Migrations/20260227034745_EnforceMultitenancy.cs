using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceMultitenancy : Migration
    {
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
            // Step 0 (no-op): The original migration seeded a 'default' tenant and
            // backfilled NULL tenant_id values for pre-existing single-tenant data.
            // Nocturne has always been multitenant — there is no legacy data to backfill,
            // so the seed and backfill are removed. Tenants are created through /setup.

            // Step 1: Make tenant_id NOT NULL and add FK on all tenant-scoped tables
            foreach (var table in TenantScopedTables)
            {
                migrationBuilder.AlterColumn<Guid>(
                    name: "tenant_id",
                    table: table,
                    type: "uuid",
                    nullable: false,
                    oldClrType: typeof(Guid),
                    oldType: "uuid",
                    oldNullable: true);

                migrationBuilder.AddForeignKey(
                    name: $"fk_{table}_tenant_id",
                    table: table,
                    column: "tenant_id",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            }

            // Step 2: Enable RLS and create tenant isolation policies
            foreach (var table in TenantScopedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                // Use missing_ok := true + NULLIF so the policy expression is
                // safe to evaluate when the GUC is unset (e.g. during EF
                // migrations running under the migrator role). An unset GUC
                // collapses to NULL, tenant_id = NULL is NULL, the row is
                // excluded — which is the correct "cannot see anything
                // without setting tenant context" semantics.
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                    """);
            }

            // Role creation (nocturne_app, nocturne_migrator) used to live
            // here but was moved to the Postgres container init script
            // (docs/postgres/container-init/00-init.sh and the BYO
            // bootstrap-roles.sql). Migrations run as nocturne_migrator,
            // which has NOCREATEROLE — it cannot create or alter roles.
            // The init script runs before any migration, so both roles
            // already exist by the time this migration executes.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: drop policies, disable RLS, drop FKs, make tenant_id nullable
            foreach (var table in TenantScopedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");

                migrationBuilder.DropForeignKey(
                    name: $"fk_{table}_tenant_id",
                    table: table);

                migrationBuilder.AlterColumn<Guid>(
                    name: "tenant_id",
                    table: table,
                    type: "uuid",
                    nullable: true,
                    oldClrType: typeof(Guid),
                    oldType: "uuid",
                    oldNullable: false);
            }
        }
    }
}
