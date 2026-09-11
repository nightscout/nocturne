using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveSubjectTokensToDirectGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legacy_token_digest",
                table: "oauth_grants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_legacy_token_digest",
                table: "oauth_grants",
                column: "legacy_token_digest",
                filter: "legacy_token_digest IS NOT NULL")
                .Annotation("Npgsql:IndexOperators", new[] { "varchar_pattern_ops" });

            // Carry every imported Nightscout subject token onto a direct grant before the columns
            // holding it are dropped, then take the membership away. An account per device token is
            // what OrphanedSubjectFilter classes as locked out, so the tenant answers 503
            // recovery_mode on every API request, and the recovery page cannot resolve an account
            // that never had a username.
            //
            // One row set drives both statements, so the memberships removed can never be wider than
            // the grants written. Data-modifying CTEs execute exactly once and to completion whether
            // or not the primary query reads them, and every sub-statement sees the same snapshot.
            //
            // Only oauth_grants is tenant-scoped, hence the per-tenant GUC; tenant_members,
            // tenant_roles and subjects carry no RLS policy, so the owner cursor can read them
            // before any tenant context is set.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN
                        SELECT t.id AS tenant_id,
                               -- Matches TenantOwnerFilter.OwnersOf, including its ordering: a
                               -- deactivated or system subject does not own a tenant, and a tenant
                               -- with several owners must resolve the same one the application does.
                               (SELECT om.subject_id
                                  FROM tenant_members om
                                  JOIN subjects os ON os.id = om.subject_id
                                  JOIN tenant_member_roles omr ON omr.tenant_member_id = om.id
                                  JOIN tenant_roles ot ON ot.id = omr.tenant_role_id
                                 WHERE om.tenant_id = t.id
                                   AND om.revoked_at IS NULL
                                   AND os.is_active
                                   AND NOT os.is_system_subject
                                   AND ot.slug = 'owner'
                                 ORDER BY om.sys_created_at, om.id
                                 LIMIT 1) AS owner_id
                        FROM tenants t
                    LOOP
                        CONTINUE WHEN r.owner_id IS NULL;

                        PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);

                        WITH device_memberships AS (
                            -- A membership whose subject holds a token and no way to sign in is a
                            -- device, not a person. Revoked memberships are left alone: they are
                            -- soft-deleted history, and tenant_member_roles cascades off them.
                            SELECT tm.id AS membership_id,
                                   s.name AS label,
                                   s.access_token_hash,
                                   s.legacy_token_digest,
                                   s.is_active,
                                   tm.direct_permissions
                              FROM tenant_members tm
                              JOIN subjects s ON s.id = tm.subject_id
                             WHERE tm.tenant_id = r.tenant_id
                               AND tm.revoked_at IS NULL
                               AND NOT s.is_system_subject
                               AND (s.access_token_hash IS NOT NULL
                                    OR s.legacy_token_digest IS NOT NULL)
                               AND NOT EXISTS (
                                     SELECT 1 FROM passkey_credentials p
                                      WHERE p.subject_id = s.id)
                               AND NOT EXISTS (
                                     SELECT 1 FROM subject_oidc_identities i
                                      WHERE i.subject_id = s.id)
                        ),
                        converted AS (
                            INSERT INTO oauth_grants (
                                id, tenant_id, subject_id, grant_type, scopes,
                                label, token_hash, legacy_token_digest, is_migrated, created_at
                            )
                            SELECT gen_random_uuid(),
                                   r.tenant_id,
                                   r.owner_id,
                                   'direct',
                                   -- A membership stores permissions as a jsonb array and a grant
                                   -- stores scopes as text[]; same vocabulary, different container.
                                   ARRAY(SELECT jsonb_array_elements_text(d.direct_permissions)),
                                   d.label,
                                   d.access_token_hash,
                                   d.legacy_token_digest,
                                   true,
                                   now()
                              FROM device_memberships d
                             -- The rows excluded here authorize nothing, so there is no credential
                             -- to carry over: a deactivated subject's token already refused every
                             -- request, and a membership with no permissions grants no scopes.
                             -- Reissuing either as a live grant would hand back access the instance
                             -- had taken away.
                             WHERE d.is_active
                               AND jsonb_typeof(d.direct_permissions) = 'array'
                               AND jsonb_array_length(d.direct_permissions) > 0
                            RETURNING 1
                        )
                        -- The subject rows themselves stay: audit trails point at them.
                        DELETE FROM tenant_members
                         WHERE id IN (SELECT membership_id FROM device_memberships);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_subjects_access_token_hash",
                table: "subjects");

            migrationBuilder.DropIndex(
                name: "ix_subjects_legacy_token_digest",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "access_token_hash",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "access_token_prefix",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "legacy_token_digest",
                table: "subjects");
        }

        /// <remarks>
        /// Schema only. The conversion is not reversed: the grants it created stay, the memberships
        /// it deleted are not recreated, and the token columns come back empty. Rolling back returns
        /// an instance to an image whose only reader of these tokens is gone, so every imported
        /// device stops authenticating until it rolls forward again.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oauth_grants_legacy_token_digest",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "legacy_token_digest",
                table: "oauth_grants");

            migrationBuilder.AddColumn<string>(
                name: "access_token_hash",
                table: "subjects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "access_token_prefix",
                table: "subjects",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legacy_token_digest",
                table: "subjects",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subjects_access_token_hash",
                table: "subjects",
                column: "access_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subjects_legacy_token_digest",
                table: "subjects",
                column: "legacy_token_digest",
                filter: "legacy_token_digest IS NOT NULL");
        }
    }
}
