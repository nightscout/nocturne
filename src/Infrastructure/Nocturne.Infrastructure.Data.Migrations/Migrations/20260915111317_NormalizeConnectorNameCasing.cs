using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeConnectorNameCasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per tenant, because connector_configurations is RLS-scoped and the policy excludes
            // every row while app.current_tenant_id is unset, which is how a migration connects.
            // Each statement also restricts on tenant_id: a role that bypasses RLS — a superuser on
            // a bring-your-own PostgreSQL — would otherwise see every tenant's rows on every pass
            // and rank one tenant's connector against another's, deleting live configurations.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        -- A tenant holding two spellings of one connector holds two half-configured
                        -- rows: whichever write path created each one put only its own keys there,
                        -- and reads picked between them arbitrarily. Fold them onto the row that
                        -- most recently proved it could sync, letting that row's own keys win, so
                        -- credentials saved through one path are not dropped because the other
                        -- path happened to run last.
                        WITH ranked AS (
                            SELECT
                                id,
                                lower(connector_name) AS canonical,
                                configuration,
                                secrets,
                                row_number() OVER (
                                    PARTITION BY lower(connector_name)
                                    ORDER BY last_successful_sync DESC NULLS LAST,
                                             last_sync_attempt DESC NULLS LAST,
                                             last_modified DESC NULLS LAST,
                                             sys_created_at DESC,
                                             id) AS rank
                            FROM connector_configurations
                            WHERE tenant_id = t_id
                        ),
                        -- jsonb_each raises on anything but an object, which would abort the whole
                        -- block and leave the API unable to start: migrations run before the host
                        -- does. A hand-edited row holding an array or a scalar carries nothing this
                        -- merge could use, so read it as empty instead.
                        pairs AS (
                            SELECT r.canonical, 'configuration' AS doc, e.key, e.value, r.rank
                            FROM ranked r,
                                 jsonb_each(CASE WHEN jsonb_typeof(r.configuration) = 'object'
                                                 THEN r.configuration ELSE '{}'::jsonb END) e
                            UNION ALL
                            SELECT r.canonical, 'secrets', e.key, e.value, r.rank
                            FROM ranked r,
                                 jsonb_each(CASE WHEN jsonb_typeof(r.secrets) = 'object'
                                                 THEN r.secrets ELSE '{}'::jsonb END) e
                        ),
                        winning AS (
                            SELECT DISTINCT ON (canonical, doc, key) canonical, doc, key, value
                            FROM pairs
                            ORDER BY canonical, doc, key, rank
                        ),
                        merged AS (
                            SELECT canonical, doc, jsonb_object_agg(key, value) AS document
                            FROM winning
                            GROUP BY canonical, doc
                        )
                        UPDATE connector_configurations c
                        SET configuration = coalesce(
                                (SELECT document FROM merged m
                                 WHERE m.canonical = r.canonical AND m.doc = 'configuration'),
                                c.configuration),
                            secrets = coalesce(
                                (SELECT document FROM merged m
                                 WHERE m.canonical = r.canonical AND m.doc = 'secrets'),
                                c.secrets)
                        FROM ranked r
                        WHERE r.id = c.id
                          AND r.rank = 1
                          AND EXISTS (
                              SELECT 1 FROM ranked loser
                              WHERE loser.canonical = r.canonical AND loser.rank > 1);

                        WITH ranked AS (
                            SELECT
                                id,
                                row_number() OVER (
                                    PARTITION BY lower(connector_name)
                                    ORDER BY last_successful_sync DESC NULLS LAST,
                                             last_sync_attempt DESC NULLS LAST,
                                             last_modified DESC NULLS LAST,
                                             sys_created_at DESC,
                                             id) AS rank
                            FROM connector_configurations
                            WHERE tenant_id = t_id
                        )
                        DELETE FROM connector_configurations c
                        USING ranked r
                        WHERE r.id = c.id AND r.rank > 1;

                        UPDATE connector_configurations
                        SET connector_name = lower(connector_name)
                        WHERE tenant_id = t_id AND connector_name <> lower(connector_name);
                    END LOOP;
                END $$;
                """);

            // Last, so it is checked against rows the statements above have already folded and
            // lowered — and in the same transaction, so a row they somehow missed rolls the
            // normalisation back rather than leaving the constraint to fail on every later start.
            migrationBuilder.AddCheckConstraint(
                name: "ck_connector_configurations_connector_name_lower",
                table: "connector_configurations",
                sql: "connector_name = lower(connector_name)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_connector_configurations_connector_name_lower",
                table: "connector_configurations");
        }
    }
}
