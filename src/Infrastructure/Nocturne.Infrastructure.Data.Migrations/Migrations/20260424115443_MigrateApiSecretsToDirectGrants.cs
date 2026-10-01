using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrateApiSecretsToDirectGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migrate existing tenant API secrets into DirectGrant rows.
            // Uses DO block to loop tenants and set RLS context per iteration.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN
                        SELECT t.id AS tenant_id, t.api_secret_hash, tm.subject_id AS owner_id
                        FROM tenants t
                        INNER JOIN tenant_members tm ON tm.tenant_id = t.id AND tm.revoked_at IS NULL
                        INNER JOIN tenant_member_roles tmr ON tmr.tenant_member_id = tm.id
                        INNER JOIN tenant_roles tr ON tr.id = tmr.tenant_role_id AND tr.slug = 'owner'
                        WHERE t.api_secret_hash IS NOT NULL
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);

                        INSERT INTO oauth_grants (
                            id, tenant_id, subject_id, grant_type, scopes,
                            label, legacy_secret_hash, created_at
                        )
                        VALUES (
                            gen_random_uuid(),
                            r.tenant_id,
                            r.owner_id,
                            'direct',
                            ARRAY['*'],
                            'Legacy API secret',
                            r.api_secret_hash,
                            NOW()
                        );
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("ALTER TABLE tenants DROP COLUMN IF EXISTS api_secret_hash;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "api_secret_hash",
                table: "tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN
                        SELECT t.id AS tenant_id
                        FROM tenants t
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);

                        UPDATE tenants
                        SET api_secret_hash = (
                            SELECT g.legacy_secret_hash
                            FROM oauth_grants g
                            WHERE g.tenant_id = r.tenant_id
                              AND g.grant_type = 'direct'
                              AND g.legacy_secret_hash IS NOT NULL
                              AND g.revoked_at IS NULL
                              AND g.label = 'Legacy API secret'
                            LIMIT 1
                        )
                        WHERE id = r.tenant_id;
                    END LOOP;
                END $$;
                """);
        }
    }
}
