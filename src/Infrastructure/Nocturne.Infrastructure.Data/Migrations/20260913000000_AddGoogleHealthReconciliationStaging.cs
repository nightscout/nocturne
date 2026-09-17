using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations;

[DbContext(typeof(NocturneDbContext))]
[Migration("20260913000000_AddGoogleHealthReconciliationStaging")]
public partial class AddGoogleHealthReconciliationStaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $repair$
            BEGIN
                IF to_regclass('google_health_reconciliation_runs') IS NOT NULL
                    OR to_regclass('google_health_reconciliation_ids') IS NOT NULL THEN
                    RAISE WARNING 'Replacing unregistered Google Health reconciliation staging tables. Interrupted import staging is discarded; native health data and connector settings are unchanged.';
                END IF;
            END $repair$;
            DROP TABLE IF EXISTS google_health_reconciliation_ids;
            DROP TABLE IF EXISTS google_health_reconciliation_runs;
            CREATE TABLE google_health_reconciliation_runs (
                id uuid NOT NULL PRIMARY KEY,
                tenant_id uuid NOT NULL,
                from_time timestamp with time zone NOT NULL,
                to_time timestamp with time zone NOT NULL,
                active_types text NOT NULL
            );
            CREATE TABLE google_health_reconciliation_ids (
                run_id uuid NOT NULL,
                tenant_id uuid NOT NULL,
                data_type text NOT NULL,
                identifier text NOT NULL,
                PRIMARY KEY (run_id, data_type, identifier)
            );
            CREATE INDEX ix_google_health_reconciliation_runs_tenant
                ON google_health_reconciliation_runs (tenant_id);
            CREATE INDEX ix_google_health_reconciliation_ids_tenant
                ON google_health_reconciliation_ids (tenant_id, run_id, data_type);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS google_health_reconciliation_ids;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS google_health_reconciliation_runs;");
    }
}