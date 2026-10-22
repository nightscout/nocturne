using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveAdoptedSecretsToDeviceSubject : Migration
    {
        /// <summary>
        /// An adopted Nightscout API secret belongs to the site, not to the person who happened to
        /// save it or run the migration. Held by a person it would stop every uploader when that
        /// person is removed, and an api-secret authenticates only while its holder is a member, so
        /// every live one moves to the tenant's device subject, created where a tenant has none.
        /// See <c>DeviceSubjectFilter</c> for why the holder is not a person.
        /// </summary>
        /// <remarks>
        /// Adopted secrets are the migrated grants with a <c>legacy_secret_hash</c> and no
        /// <c>token_hash</c>. A minted <c>noc_</c> grant carries both hashes and belongs to the
        /// person it was issued to. Only <c>oauth_grants</c> is tenant-scoped, hence the per-tenant
        /// GUC.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                    device_subject_id uuid;
                BEGIN
                    FOR r IN SELECT id AS tenant_id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);

                        CREATE TEMP TABLE adopted_secrets AS
                            SELECT g.id
                              FROM oauth_grants g
                              JOIN subjects s ON s.id = g.subject_id
                             WHERE g.tenant_id = r.tenant_id
                               AND g.grant_type = 'direct'
                               AND g.is_migrated
                               AND g.legacy_secret_hash IS NOT NULL
                               AND g.token_hash IS NULL
                               AND g.revoked_at IS NULL
                               AND NOT (s.is_system_subject AND s.name = 'Devices');

                        IF NOT EXISTS (SELECT 1 FROM adopted_secrets) THEN
                            DROP TABLE adopted_secrets;
                            CONTINUE;
                        END IF;

                        device_subject_id := NULL;

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

                        UPDATE oauth_grants
                           SET subject_id = device_subject_id
                         WHERE id IN (SELECT id FROM adopted_secrets);

                        DROP TABLE adopted_secrets;
                    END LOOP;
                END $$;
                """);
        }

        /// <remarks>
        /// Not reversed: the grants' previous holders are not recorded.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
