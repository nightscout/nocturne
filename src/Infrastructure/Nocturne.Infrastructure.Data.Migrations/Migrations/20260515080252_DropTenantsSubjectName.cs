using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropTenantsSubjectName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill the canonical home (PatientRecord.PreferredName) from the legacy
            // tenants.subject_name BEFORE dropping the column. patient_records is
            // RLS-protected so the migrator must set app.current_tenant_id per iteration;
            // the tenants table is not RLS-protected so it reads fine without context.
            // Only overwrite when the patient record's preferred_name is still null —
            // a user who has already set their preferred name in the UI keeps that value.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN
                        SELECT id, subject_name
                        FROM tenants
                        WHERE subject_name IS NOT NULL AND subject_name <> ''
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);

                        UPDATE patient_records
                        SET preferred_name = r.subject_name
                        WHERE preferred_name IS NULL;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "subject_name",
                table: "tenants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subject_name",
                table: "tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }
    }
}
