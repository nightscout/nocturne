using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeStoredGrantScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MoveSubjectTokensToDirectGrants copied role permissions onto device grants verbatim, so
            // some hold tenant-administration atoms a grant can never exercise. Strip every scope
            // outside Scope.ValidRequestScopes, as of this migration. A grant left with none authorizes
            // nothing, and MoveSubjectTokensToDirectGrants never created one, so it is revoked. FORCE
            // ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    valid text[] := ARRAY[
                        'glucose.read', 'glucose.readwrite',
                        'treatments.read', 'treatments.readwrite',
                        'devices.read', 'devices.readwrite',
                        'therapy.read', 'therapy.readwrite',
                        'alerts.read', 'alerts.readwrite', 'alerts.acknowledge',
                        'reports.read',
                        'identity.read',
                        'heartrate.read', 'heartrate.readwrite',
                        'stepcount.read', 'stepcount.readwrite',
                        'sleep.read', 'sleep.readwrite',
                        'food.read', 'food.readwrite',
                        'sharing.readwrite',
                        'device.notify', 'device.actuate',
                        '*', 'health.read', 'health.readwrite'
                    ];
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE oauth_grants g
                        SET scopes = f.kept,
                            revoked_at = CASE WHEN cardinality(f.kept) = 0
                                              THEN coalesce(g.revoked_at, now())
                                              ELSE g.revoked_at END
                        FROM (
                            SELECT id,
                                   ARRAY(
                                     SELECT s.scope
                                     FROM unnest(scopes) WITH ORDINALITY AS s(scope, position)
                                     WHERE s.scope = ANY(valid)
                                     ORDER BY s.position) AS kept
                            FROM oauth_grants
                            WHERE NOT scopes <@ valid
                        ) f
                        WHERE g.id = f.id;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The removed atoms authorized nothing, so there is nothing to restore.
        }
    }
}
