using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations;

[Migration("20260913000000_AddGoogleHealthReconciliationStaging")]
public partial class AddGoogleHealthReconciliationStaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS google_health_reconciliation_runs (
                id uuid NOT NULL PRIMARY KEY,
                tenant_id uuid NOT NULL,
                from_time timestamp with time zone NOT NULL,
                to_time timestamp with time zone NOT NULL,
                active_types text NOT NULL
            );
            CREATE TABLE IF NOT EXISTS google_health_reconciliation_ids (
                run_id uuid NOT NULL,
                tenant_id uuid NOT NULL,
                data_type text NOT NULL,
                identifier text NOT NULL,
                PRIMARY KEY (run_id, data_type, identifier)
            );
            CREATE INDEX IF NOT EXISTS ix_google_health_reconciliation_runs_tenant
                ON google_health_reconciliation_runs (tenant_id);
            CREATE INDEX IF NOT EXISTS ix_google_health_reconciliation_ids_tenant
                ON google_health_reconciliation_ids (tenant_id, run_id, data_type);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS google_health_reconciliation_ids;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS google_health_reconciliation_runs;");
    }
}