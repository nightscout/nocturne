using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillSensorGlucosePatientDeviceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT DISTINCT tenant_id FROM patient_devices WHERE device_category = 'CGM'
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

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
            migrationBuilder.Sql(
                "UPDATE sensor_glucose SET patient_device_id = NULL WHERE patient_device_id IS NOT NULL;");
        }
    }
}
