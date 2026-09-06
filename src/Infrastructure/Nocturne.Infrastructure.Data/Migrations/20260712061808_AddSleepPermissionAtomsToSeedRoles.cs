using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Appends the new sleep.read/sleep.readwrite permission atoms to existing seed role
    /// rows. Seed roles are persisted per-tenant and SeedRolesForTenantAsync skips slugs
    /// that already exist, so tenants created before the atoms were added never receive
    /// them without a data migration (same backfill as heartrate/stepcount/food in
    /// RenamePermissionAtoms). Mirrors RoleSeeds.Permissions: admin gets
    /// readwrite, caretaker and clinician get read.
    /// </summary>
    public partial class AddSleepPermissionAtomsToSeedRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // tenant_roles is under FORCE ROW LEVEL SECURITY: loop over tenants and set
            // the tenant GUC per iteration so the policies admit the updates.
            migrationBuilder.Sql("""
                DO $$
                DECLARE t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        UPDATE tenant_roles
                        SET permissions = permissions || '["sleep.read"]'::jsonb
                        WHERE slug IN ('caretaker', 'clinician') AND is_system = true
                          AND NOT permissions @> '["sleep.read"]';

                        UPDATE tenant_roles
                        SET permissions = permissions || '["sleep.read", "sleep.readwrite"]'::jsonb
                        WHERE slug = 'admin' AND is_system = true
                          AND NOT permissions @> '["sleep.read"]';
                    END LOOP;
                END $$;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        UPDATE tenant_roles
                        SET permissions = (
                            SELECT jsonb_agg(elem)
                            FROM jsonb_array_elements(permissions) AS elem
                            WHERE elem #>> '{}' NOT IN ('sleep.read', 'sleep.readwrite')
                        )
                        WHERE slug IN ('caretaker', 'clinician', 'admin') AND is_system = true;
                    END LOOP;
                END $$;
            """);
        }
    }
}
