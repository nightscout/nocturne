using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRlsToNewTenantTables : Migration
    {
        /// <summary>
        /// Tenant-scoped tables added after the original EnforceMultitenancy
        /// migration (2026-02-27) that were missing Row Level Security. All
        /// implement <c>ITenantScoped</c> but were never enrolled in the RLS
        /// regime.
        ///
        /// Role creation, ownership, grants and ALTER DEFAULT PRIVILEGES
        /// that were previously in this migration were moved to the Postgres
        /// init script (docs/postgres/container-init/00-init.sh and
        /// docs/postgres/bootstrap-roles.sql) because those operations
        /// require CREATEROLE / superuser privileges that the migrator role
        /// intentionally does not have. The init script runs before any
        /// migration, so by the time this migration runs nocturne_migrator
        /// already owns the schema and default privileges for nocturne_app
        /// are already in place.
        /// </summary>
        private static readonly string[] NewTenantScopedTables =
        [
            // Added 2026-02-27 (same day as EnforceMultitenancy, missed the list)
            "system_events",
            // Added 2026-03-20 in AddPatientRecordTables
            "patient_records", "patient_devices", "patient_insulins", "devices",
            // Added 2026-03-22 in the new alert engine. alert_rules was
            // dropped in DropOldAlertTables and recreated 8 minutes later
            // in AddAlertEngineTables with a new schema, so despite sharing
            // a name with the old table it is a distinct post-RLS table.
            "alert_rules",
            "alert_schedules", "alert_escalation_steps", "alert_step_channels",
            "alert_tracker_state", "alert_excursions", "alert_instances",
            "alert_deliveries", "alert_invites", "alert_custom_sounds",
            // Added 2026-04-06
            "body_weights",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Enable + force RLS on the tables that were missed, and add
            // tenant_isolation policies with USING and WITH CHECK so both
            // reads and writes are enforced. NULLIF + missing_ok keeps the
            // policy expression safe to evaluate when the GUC is unset (e.g.
            // during EF migrations running under the migrator role).
            foreach (var table in NewTenantScopedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"""
                    DROP POLICY IF EXISTS tenant_isolation ON {table};
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in NewTenantScopedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
