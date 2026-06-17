using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsMigratedToOAuthGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_migrated",
                table: "oauth_grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: every grant carrying a SHA-1 secret hash at this point was seeded from a
            // pre-existing Nightscout master secret (migration or Nightscout connector). Minted
            // noc_ tokens only start populating legacy_secret_hash from this release onward, so the
            // condition cleanly distinguishes existing legacy grants. oauth_grants is RLS-protected
            // with FORCE ROW LEVEL SECURITY, so a data UPDATE must run with the tenant context set
            // per row's tenant; loop tenants and set app.current_tenant_id each iteration.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);

                        UPDATE oauth_grants
                        SET is_migrated = true
                        WHERE tenant_id = r.id
                          AND legacy_secret_hash IS NOT NULL;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_migrated",
                table: "oauth_grants");
        }
    }
}
