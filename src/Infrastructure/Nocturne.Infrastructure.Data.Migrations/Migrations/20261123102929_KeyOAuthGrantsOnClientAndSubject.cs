using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets a subject hold one unrevoked app grant per client within a tenant, as
    /// <c>GrantConsentService</c> already treats it: a consent merges into the grant it finds. Direct
    /// and guest grants carry no client, so only app grants are keyed. Every loser of a duplicate key
    /// is revoked first, and its refresh tokens and registered devices move to the winner, so no app
    /// is signed out; the rows stay as the audit trail. Revoked tokens move too, so a replay of a
    /// rotated ancestor still revokes the grant its live descendant is on. The winner is the grant created
    /// last, then the highest id. A moved token takes the winner's scopes on its next refresh, and
    /// the subject's way back to a wider set is to consent again.
    /// </summary>
    public partial class KeyOAuthGrantsOnClientAndSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   first_value(id) OVER (
                                       PARTITION BY tenant_id, client_id, subject_id
                                       ORDER BY created_at DESC, id DESC) AS winner_id,
                                   row_number() OVER (
                                       PARTITION BY tenant_id, client_id, subject_id
                                       ORDER BY created_at DESC, id DESC) AS rn
                            FROM oauth_grants
                            WHERE tenant_id = t.id
                              AND grant_type = 'app'
                              AND client_id IS NOT NULL
                              AND revoked_at IS NULL
                        ),
                        losers AS (
                            SELECT id, winner_id FROM ranked WHERE rn > 1
                        ),
                        moved_tokens AS (
                            UPDATE oauth_refresh_tokens rt SET grant_id = losers.winner_id
                            FROM losers
                            WHERE rt.tenant_id = t.id
                              AND rt.grant_id = losers.id
                            RETURNING rt.id
                        ),
                        moved_devices AS (
                            UPDATE client_devices cd SET grant_id = losers.winner_id
                            FROM losers
                            WHERE cd.tenant_id = t.id
                              AND cd.grant_id = losers.id
                            RETURNING cd.id
                        )
                        UPDATE oauth_grants g SET revoked_at = now()
                        FROM losers
                        WHERE g.tenant_id = t.id AND g.id = losers.id;
                    END LOOP;
                END $$;
                """);

            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_oauth_grants_tenant_client_subject_active",
                "ON oauth_grants (tenant_id, client_id, subject_id) WHERE grant_type = 'app' AND revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_oauth_grants_tenant_client_subject_active");
        }
    }
}
