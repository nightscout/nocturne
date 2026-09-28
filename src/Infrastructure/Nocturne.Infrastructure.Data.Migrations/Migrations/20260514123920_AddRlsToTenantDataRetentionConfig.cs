using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRlsToTenantDataRetentionConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The AddTenantDataRetentionConfig migration was initially created without RLS
            // and subsequently edited to add it. This migration idempotently applies RLS
            // so that existing databases (dev, production) are corrected without requiring
            // the table to be dropped and recreated.
            migrationBuilder.Sql("ALTER TABLE tenant_data_retention_config ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenant_data_retention_config FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DO $$ BEGIN
                  IF NOT EXISTS (
                    SELECT 1 FROM pg_policy
                    WHERE polrelid = 'tenant_data_retention_config'::regclass
                      AND polname = 'tenant_isolation'
                  ) THEN
                    CREATE POLICY tenant_isolation ON tenant_data_retention_config
                      USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                      WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                  END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON tenant_data_retention_config;");
            migrationBuilder.Sql("ALTER TABLE tenant_data_retention_config NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenant_data_retention_config DISABLE ROW LEVEL SECURITY;");
        }
    }
}
