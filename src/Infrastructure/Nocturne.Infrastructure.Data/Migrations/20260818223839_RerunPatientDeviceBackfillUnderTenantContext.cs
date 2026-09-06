using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RerunPatientDeviceBackfillUnderTenantContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BackfillSensorGlucosePatientDeviceId drove its loop off patient_devices, which is
            // tenant-scoped under FORCE ROW LEVEL SECURITY. The migrator role is NOBYPASSRLS and no
            // tenant context is set when the loop's driving SELECT runs, so it returned no rows and
            // the backfill never executed. tenants carries no RLS, so it can drive the loop.
            // The UPDATE is unchanged and is idempotent through its patient_device_id IS NULL guard.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE sensor_glucose sg
                        SET patient_device_id = pd.id
                        FROM patient_devices pd
                        WHERE pd.device_category = 'CGM'
                          AND sg.patient_device_id IS NULL
                          AND (
                              (sg.data_source = 'dexcom-connector' AND pd.manufacturer ILIKE 'dexcom')
                              OR (sg.data_source = 'libre-connector' AND pd.manufacturer ILIKE 'abbott')
                              OR (sg.data_source = 'minimed-connector' AND pd.manufacturer ILIKE 'medtronic')
                          )
                          AND (pd.start_date IS NULL OR sg.timestamp >= pd.start_date::timestamp)
                          AND (pd.end_date IS NULL OR sg.timestamp <= (pd.end_date + 1)::timestamp);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: a backfilled patient_device_id cannot be told apart from one set by
            // normal ingestion, so clearing them would destroy organically attributed rows.
        }
    }
}
