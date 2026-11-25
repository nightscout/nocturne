using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Makes a <c>retire-invalid-treatments</c> row due now for every tenant holding a live temp basal,
    /// profile activation, or override or temporary target span that kept <c>isValid: false</c>, so the
    /// job deletes each as its client's <c>isValid: false</c> deletes it.
    /// </summary>
    /// <remarks>
    /// The delete settles the spans and activations around each record and hands on its duplicate group,
    /// which only the runtime delete does, so the retiring is the job and not SQL. FORCE ROW LEVEL
    /// SECURITY hides every tenant's rows from the migrator until its tenant is set. The ledger row is
    /// upserted, so running it again leaves one due row per tenant.
    /// </remarks>
    public partial class RetireInvalidUploadedTreatmentRows : Migration
    {
        internal const string JobName = "retire-invalid-treatments";

        internal const string RequestSql = $"""
            DO $$
            DECLARE
                t RECORD;
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);

                    CONTINUE WHEN NOT (
                        EXISTS (
                            SELECT 1 FROM temp_basals
                            WHERE tenant_id = t.id AND deleted_at IS NULL AND legacy_id IS NOT NULL
                              AND (additional_properties -> 'isValid') = 'false'::jsonb)
                        OR EXISTS (
                            SELECT 1 FROM profile_activations
                            WHERE tenant_id = t.id AND deleted_at IS NULL AND legacy_id IS NOT NULL
                              AND (additional_properties -> 'isValid') = 'false'::jsonb)
                        OR EXISTS (
                            SELECT 1 FROM state_spans
                            WHERE tenant_id = t.id AND deleted_at IS NULL AND original_id IS NOT NULL
                              AND category IN ('Override', 'TemporaryTarget')
                              AND (metadata -> 'isValid') = 'false'::jsonb));

                    INSERT INTO job_ledger (id, job, tenant_id, next_due_at, attempts)
                    VALUES ({AddJobLedger.UuidV7}, '{JobName}', t.id, now(), 0)
                    ON CONFLICT (job, tenant_id) DO UPDATE SET next_due_at = now();
                END LOOP;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RequestSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DELETE FROM job_ledger WHERE job = '{JobName}';");
        }
    }
}
