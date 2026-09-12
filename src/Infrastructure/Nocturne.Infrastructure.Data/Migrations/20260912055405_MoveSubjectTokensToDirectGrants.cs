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

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "oauth_grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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
            // The tokens move to the tenant's device subject, created here on the tenants that need
            // one. See DeviceSubjectFilter for why the holder is not a person; the short of it is
            // that a token issued to somebody inherits what that somebody is.
            //
            // One row set drives both statements, so the memberships removed can never be wider
            // than the grants written. Only oauth_grants is tenant-scoped, hence the per-tenant
            // GUC; tenant_members, tenant_roles and subjects carry no RLS policy.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                    device_subject_id uuid;
                BEGIN
                    FOR r IN SELECT id AS tenant_id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);

                        CREATE TEMP TABLE device_memberships AS
                            -- A membership whose subject holds a token and no way to sign in is a
                            -- device, not a person. Revoked memberships are left alone: they are
                            -- soft-deleted history, and tenant_member_roles cascades off them.
                            --
                            -- The scopes are the union of the membership's own permissions and
                            -- those of its tenant roles, which is what MemberScopeMiddleware
                            -- resolves. Reading direct_permissions alone would silently drop the
                            -- authority of a device whose access came from a role. The CASE guards
                            -- are load-bearing: jsonb_array_length and jsonb_array_elements_text
                            -- both error on a scalar, and AND does not short-circuit in SQL, so a
                            -- jsonb_typeof test in the WHERE clause does not protect them.
                            SELECT tm.id AS membership_id,
                                   s.name AS label,
                                   s.access_token_hash,
                                   s.legacy_token_digest,
                                   s.is_active,
                                   tm.limit_to_24_hours,
                                   ARRAY(
                                     SELECT DISTINCT p FROM (
                                       SELECT jsonb_array_elements_text(
                                                CASE WHEN jsonb_typeof(tm.direct_permissions) = 'array'
                                                     THEN tm.direct_permissions
                                                     ELSE '[]'::jsonb END) AS p
                                       UNION
                                       SELECT perm
                                         FROM tenant_member_roles mr
                                         JOIN tenant_roles tr ON tr.id = mr.tenant_role_id
                                         CROSS JOIN LATERAL jsonb_array_elements_text(
                                                CASE WHEN jsonb_typeof(tr.permissions) = 'array'
                                                     THEN tr.permissions
                                                     ELSE '[]'::jsonb END) AS perm
                                        WHERE mr.tenant_member_id = tm.id
                                     ) x
                                   ) AS scopes
                              FROM tenant_members tm
                              JOIN subjects s ON s.id = tm.subject_id
                             WHERE tm.tenant_id = r.tenant_id
                               AND tm.revoked_at IS NULL
                               AND NOT s.is_system_subject
                               AND NOT s.is_demo_subject
                               AND (s.access_token_hash IS NOT NULL
                                    OR s.legacy_token_digest IS NOT NULL)
                               AND NOT EXISTS (
                                     SELECT 1 FROM passkey_credentials p
                                      WHERE p.subject_id = s.id)
                               AND NOT EXISTS (
                                     SELECT 1 FROM subject_oidc_identities i
                                      WHERE i.subject_id = s.id);

                        IF NOT EXISTS (SELECT 1 FROM device_memberships) THEN
                            DROP TABLE device_memberships;
                            CONTINUE;
                        END IF;

                        SELECT s.id INTO device_subject_id
                          FROM tenant_members tm
                          JOIN subjects s ON s.id = tm.subject_id
                         WHERE tm.tenant_id = r.tenant_id
                           AND s.is_system_subject
                           AND s.name = 'Devices'
                         LIMIT 1;

                        IF device_subject_id IS NULL THEN
                            device_subject_id := gen_random_uuid();

                            INSERT INTO subjects (
                                id, name, notes, is_active, is_system_subject,
                                created_at, updated_at, approval_status)
                            VALUES (
                                device_subject_id, 'Devices',
                                'Holds the API tokens for this site. The scopes on each token '
                                  || 'decide what it can do.',
                                true, true, now(), now(), 'Approved');

                            INSERT INTO tenant_members (
                                id, tenant_id, subject_id, direct_permissions,
                                sys_created_at, sys_updated_at, limit_to_24_hours)
                            VALUES (
                                gen_random_uuid(), r.tenant_id, device_subject_id,
                                '["*"]'::jsonb, now(), now(), false);
                        END IF;

                        WITH converted AS (
                            INSERT INTO oauth_grants (
                                id, tenant_id, subject_id, grant_type, scopes, label,
                                token_hash, legacy_token_digest, limit_to_24_hours,
                                is_migrated, created_at
                            )
                            SELECT gen_random_uuid(),
                                   r.tenant_id,
                                   device_subject_id,
                                   'direct',
                                   d.scopes,
                                   d.label,
                                   d.access_token_hash,
                                   d.legacy_token_digest,
                                   -- A device its owner held to a recent window must stay held to
                                   -- it. The flag lives on the credential rather than the holder,
                                   -- because one holder carries every token on the tenant.
                                   d.limit_to_24_hours,
                                   true,
                                   now()
                              FROM device_memberships d
                             -- The rows excluded here authorize nothing, so there is no
                             -- credential to carry over: a deactivated subject's token already
                             -- refused every request, and a membership that resolves to no scopes
                             -- grants none. Their memberships still go, because a device is not an
                             -- account and leaving one behind keeps the tenant in recovery mode.
                             WHERE d.is_active
                               AND cardinality(d.scopes) > 0
                            RETURNING 1
                        )
                        -- Active memberships only: converted, or resolving to no scopes and so
                        -- carrying nothing to convert. A deactivated device is parked rather than
                        -- retired, and it is not an orphan either, so its membership stays and
                        -- reactivating it still finds one. The subject rows themselves stay in
                        -- every case: audit trails point at them.
                        DELETE FROM tenant_members
                         WHERE id IN (SELECT membership_id FROM device_memberships
                                       WHERE is_active);

                        DROP TABLE device_memberships;
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
        /// Schema only, and lossy. The grants stay but lose their <c>legacy_token_digest</c> with
        /// the column, so on a roll forward only the exact token the source instance issued still
        /// resolves and the other prefixes Nightscout would have accepted do not. The memberships
        /// are not recreated and the subject columns come back empty, so on the older image every
        /// imported device stops authenticating entirely.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oauth_grants_legacy_token_digest",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "legacy_token_digest",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
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
