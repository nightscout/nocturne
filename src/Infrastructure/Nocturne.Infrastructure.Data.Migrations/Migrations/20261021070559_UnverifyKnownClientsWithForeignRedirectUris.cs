using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnverifyKnownClientsWithForeignRedirectUris : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These known-directory entries list no redirect URIs, so a seeded row carrying some took
            // them from an anonymous registration. Such a row is the registrant's client, not the
            // directory's: it loses the verified mark and releases the software_id so the directory
            // identity can be registered again. FORCE ROW LEVEL SECURITY binds the migrator too,
            // hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE oauth_clients
                        SET is_known = false,
                            software_id = NULL,
                            updated_at = now()
                        WHERE is_known
                          AND redirect_uris <> '[]'
                          AND software_id IN (
                              'org.nightscout.cgm-remote-monitor',
                              'com.tandemdiabetes.sugarmate',
                              'se.cornixit.nightwatch',
                              'com.nocturne.follower',
                              'io.home-assistant.nocturne');
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
