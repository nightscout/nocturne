using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToOAuthRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add tenant_id as nullable first so we can backfill
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oauth_refresh_tokens",
                type: "uuid",
                nullable: true);

            // Backfill from the parent grant. The migrator role is NOBYPASSRLS,
            // so reading oauth_grants requires setting app.current_tenant_id per
            // tenant — without it, FORCE RLS on oauth_grants silently filters
            // every row and the subsequent NOT NULL alter fails.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);

                        UPDATE oauth_refresh_tokens rt
                        SET tenant_id = g.tenant_id
                        FROM oauth_grants g
                        WHERE rt.grant_id = g.id
                          AND g.tenant_id = r.id;
                    END LOOP;
                END $$;
                """);

            // Make NOT NULL now that all rows are backfilled
            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                table: "oauth_refresh_tokens",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_refresh_tokens_tenant_id",
                table: "oauth_refresh_tokens",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_refresh_tokens_tenants_tenant_id",
                table: "oauth_refresh_tokens",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // Enable Row Level Security
            migrationBuilder.Sql("ALTER TABLE oauth_refresh_tokens ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE oauth_refresh_tokens FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON oauth_refresh_tokens
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON oauth_refresh_tokens;");
            migrationBuilder.Sql("ALTER TABLE oauth_refresh_tokens DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropForeignKey(
                name: "FK_oauth_refresh_tokens_tenants_tenant_id",
                table: "oauth_refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_oauth_refresh_tokens_tenant_id",
                table: "oauth_refresh_tokens");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oauth_refresh_tokens");
        }
    }
}
