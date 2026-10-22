using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MarkDeviceApprovedGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "approved_by_device_code",
                table: "oauth_grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // A known client with no redirect URIs can only have reached a grant through the device
            // flow, and a device code still awaiting cleanup names the grant it approved. Other
            // device approvals left no trace. FORCE ROW LEVEL SECURITY binds the migrator too,
            // hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE oauth_grants g
                        SET approved_by_device_code = true
                        FROM oauth_clients c
                        WHERE g.client_id = c.id
                          AND c.is_known
                          AND (c.redirect_uris = '[]'
                               OR EXISTS (SELECT 1 FROM oauth_device_codes d WHERE d.grant_id = g.id));
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approved_by_device_code",
                table: "oauth_grants");
        }
    }
}
