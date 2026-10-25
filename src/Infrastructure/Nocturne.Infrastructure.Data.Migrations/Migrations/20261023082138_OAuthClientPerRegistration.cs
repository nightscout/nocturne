using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class OAuthClientPerRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Known-directory clients were seeded into every tenant with no created_from_ip, which a
            // dynamic registration always records. A seed nothing has used is deleted; one that holds a
            // grant or a code stays, since an installed app may still hold its client_id. FORCE ROW
            // LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        DELETE FROM oauth_clients c
                        WHERE c.is_known
                          AND c.created_from_ip IS NULL
                          AND c.software_id IN (
                              'org.nightscout.trio',
                              'com.eveningoutpost.dexdrip',
                              'org.loopkit.loop',
                              'info.nightscout.androidaps',
                              'org.nightscout.cgm-remote-monitor',
                              'com.tandemdiabetes.sugarmate',
                              'se.cornixit.nightwatch',
                              'com.nocturne.follower',
                              'dev.nocturne.prelude',
                              'com.nocturne.widget.windows',
                              'com.nocturne.tray',
                              'io.home-assistant.nocturne')
                          AND NOT EXISTS (SELECT 1 FROM oauth_grants g WHERE g.client_id = c.id)
                          AND NOT EXISTS (SELECT 1 FROM oauth_authorization_codes a WHERE a.client_entity_id = c.id)
                          AND NOT EXISTS (SELECT 1 FROM oauth_device_codes d WHERE d.client_id = c.client_id);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_oauth_clients_tenant_software_id",
                table: "oauth_clients");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_tenant_software_id",
                table: "oauth_clients",
                columns: new[] { "tenant_id", "software_id" },
                filter: "\"software_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oauth_clients_tenant_software_id",
                table: "oauth_clients");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_tenant_software_id",
                table: "oauth_clients",
                columns: new[] { "tenant_id", "software_id" },
                unique: true,
                filter: "\"software_id\" IS NOT NULL");
        }
    }
}
