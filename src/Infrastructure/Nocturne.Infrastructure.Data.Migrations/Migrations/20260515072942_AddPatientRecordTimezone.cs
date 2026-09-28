using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientRecordTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "timezone",
                table: "patient_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Backfill from the legacy per-profile therapy_settings.timezone column. patient_records
            // and therapy_settings are both tenant-scoped under FORCE ROW LEVEL SECURITY, so the
            // migrator must set app.current_tenant_id per iteration; without it the SELECT silently
            // returns zero rows and no backfill happens (RLS doesn't error, it just filters).
            // tenants.timezone and tenant_alert_settings.timezone are skipped — both default to
            // 'UTC' on row creation, so a non-null value there is indistinguishable from "user
            // never set it". Patients whose only legacy tz lived in those columns will land with
            // NULL here and pick up their real tz on next patient-record save in the UI.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    r RECORD;
                    legacy_tz TEXT;
                BEGIN
                    FOR r IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);

                        SELECT ts.timezone INTO legacy_tz
                        FROM therapy_settings ts
                        WHERE ts.timezone IS NOT NULL AND ts.timezone <> ''
                        ORDER BY ts.timestamp DESC
                        LIMIT 1;

                        IF legacy_tz IS NOT NULL THEN
                            UPDATE patient_records
                            SET timezone = legacy_tz
                            WHERE timezone IS NULL;
                        END IF;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "timezone",
                table: "patient_records");
        }
    }
}
