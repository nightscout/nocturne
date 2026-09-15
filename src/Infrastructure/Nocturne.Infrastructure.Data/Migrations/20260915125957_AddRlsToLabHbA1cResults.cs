using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRlsToLabHbA1cResults : Migration
    {
        /// <summary>
        /// Enrols <c>lab_hba1c_results</c> in the RLS regime. Its <c>ITenantScoped</c> makes the
        /// startup share-RLS reconciler add the RESTRICTIVE share_category_read policy
        /// automatically, but a restrictive policy alone denies every row — the permissive
        /// tenant_isolation policy created here is what lets a tenant read and write its own
        /// lab results (its absence caused every insert to fail with "new row violates
        /// row-level security policy").
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NULLIF + missing_ok keeps the policy expression safe to evaluate when the GUC is
            // unset, matching every other tenant-scoped table.
            migrationBuilder.Sql("ALTER TABLE lab_hba1c_results ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE lab_hba1c_results FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON lab_hba1c_results;
                CREATE POLICY tenant_isolation ON lab_hba1c_results
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON lab_hba1c_results;");
            migrationBuilder.Sql("ALTER TABLE lab_hba1c_results NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE lab_hba1c_results DISABLE ROW LEVEL SECURITY;");
        }
    }
}
