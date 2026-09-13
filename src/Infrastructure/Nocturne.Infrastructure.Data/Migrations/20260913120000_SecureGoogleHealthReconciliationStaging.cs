using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Nocturne.Infrastructure.Data.Migrations;

[DbContext(typeof(NocturneDbContext))]
[Migration("20260913120000_SecureGoogleHealthReconciliationStaging")]
public class SecureGoogleHealthReconciliationStaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "created_at", table: "google_health_reconciliation_runs",
            type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP");
        migrationBuilder.CreateIndex(
            name: "ix_google_health_reconciliation_runs_expiry",
            table: "google_health_reconciliation_runs", columns: ["tenant_id", "created_at"]);
        migrationBuilder.Sql("""
            DELETE FROM google_health_reconciliation_ids ids
            WHERE NOT EXISTS (SELECT 1 FROM google_health_reconciliation_runs run
                              WHERE run.id = ids.run_id AND run.tenant_id = ids.tenant_id);
            ALTER TABLE google_health_reconciliation_ids
                ADD CONSTRAINT fk_google_health_reconciliation_run
                FOREIGN KEY (run_id) REFERENCES google_health_reconciliation_runs (id) ON DELETE CASCADE;
            ALTER TABLE google_health_reconciliation_runs ENABLE ROW LEVEL SECURITY;
            ALTER TABLE google_health_reconciliation_runs FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON google_health_reconciliation_runs
                USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
            ALTER TABLE google_health_reconciliation_ids ENABLE ROW LEVEL SECURITY;
            ALTER TABLE google_health_reconciliation_ids FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON google_health_reconciliation_ids
                USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY tenant_isolation ON google_health_reconciliation_ids;
            ALTER TABLE google_health_reconciliation_ids DISABLE ROW LEVEL SECURITY;
            DROP POLICY tenant_isolation ON google_health_reconciliation_runs;
            ALTER TABLE google_health_reconciliation_runs DISABLE ROW LEVEL SECURITY;
            ALTER TABLE google_health_reconciliation_ids DROP CONSTRAINT fk_google_health_reconciliation_run;
            """);
        migrationBuilder.DropIndex("ix_google_health_reconciliation_runs_expiry", "google_health_reconciliation_runs");
        migrationBuilder.DropColumn("created_at", "google_health_reconciliation_runs");
    }
}