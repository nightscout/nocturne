using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DecompositionBatchRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE decomposition_batches ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE decomposition_batches FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON decomposition_batches;
                CREATE POLICY tenant_isolation ON decomposition_batches
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON decomposition_batches;");
            migrationBuilder.Sql("ALTER TABLE decomposition_batches NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE decomposition_batches DISABLE ROW LEVEL SECURITY;");
        }
    }
}
