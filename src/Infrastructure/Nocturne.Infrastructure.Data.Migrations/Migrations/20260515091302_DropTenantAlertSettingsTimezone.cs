using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropTenantAlertSettingsTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill the canonical home (PatientRecord.Timezone) from the legacy DND
            // timezone where the user actually customised it. Unlike tenants.timezone (which
            // we skipped because every row defaults to 'UTC' on insert and a non-null value
            // there is indistinguishable from "never set"), tenant_alert_settings.timezone
            // is only set via the DND PUT, so a non-'UTC' value here is genuinely user
            // intent worth preserving — though only when the patient record's timezone is
            // still null. patient_records and tenant_alert_settings are both tenant-scoped
            // under FORCE RLS, so the migrator must set app.current_tenant_id per iteration.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN
                        SELECT tenant_id, timezone
                        FROM tenant_alert_settings
                        WHERE timezone IS NOT NULL AND timezone <> '' AND timezone <> 'UTC'
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);

                        UPDATE patient_records
                        SET timezone = r.timezone
                        WHERE timezone IS NULL;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "timezone",
                table: "tenant_alert_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "timezone",
                table: "tenant_alert_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }
    }
}
