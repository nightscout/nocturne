using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenamePermissionAtoms : Migration
    {
        private static readonly (string Old, string New)[] AtomRenames =
        [
            ("entries.read", "glucose.read"),
            ("entries.readwrite", "glucose.readwrite"),
            ("devicestatus.read", "devices.read"),
            ("devicestatus.readwrite", "devices.readwrite"),
            ("profile.read", "therapy.read"),
            ("profile.readwrite", "therapy.readwrite"),
            ("notifications.read", "alerts.read"),
            ("notifications.readwrite", "alerts.readwrite"),
            ("health.read", "statistics.read"),
        ];

        private static readonly (string Table, string Column)[] JsonbColumns =
        [
            ("tenant_roles", "permissions"),
            ("tenant_members", "direct_permissions"),
            ("member_invites", "direct_permissions"),
        ];

        private static readonly (string Table, string Column)[] TextArrayColumns =
        [
            ("oauth_grants", "scopes"),
            ("oauth_authorization_codes", "scopes"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var updateStatements = new System.Text.StringBuilder();

            // Atom renames across JSONB permission columns
            foreach (var (table, column) in JsonbColumns)
            {
                foreach (var (oldAtom, newAtom) in AtomRenames)
                {
                    updateStatements.AppendLine($$"""
                        UPDATE {{table}}
                        SET {{column}} = (
                            SELECT jsonb_agg(
                                CASE WHEN elem #>> '{}' = '{{oldAtom}}' THEN '"{{newAtom}}"'::jsonb
                                     ELSE elem END
                            )
                            FROM jsonb_array_elements({{column}}) AS elem
                        )
                        WHERE {{column}} @> '["{{oldAtom}}"]';
                    """);
                }
            }

            // Atom renames across text[] scope columns
            foreach (var (table, column) in TextArrayColumns)
            {
                foreach (var (oldAtom, newAtom) in AtomRenames)
                {
                    updateStatements.AppendLine($$"""
                        UPDATE {{table}}
                        SET {{column}} = array_replace({{column}}, '{{oldAtom}}', '{{newAtom}}')
                        WHERE '{{oldAtom}}' = ANY({{column}});
                    """);
                }
            }

            // Seed role slug/name renames
            updateStatements.AppendLine("""
                UPDATE tenant_roles SET slug = 'viewer', name = 'Viewer'
                WHERE slug = 'follower' AND is_system = true;

                UPDATE tenant_roles SET slug = 'clinician', name = 'Clinician'
                WHERE slug = 'readable' AND is_system = true;
            """);

            // Add new atoms to seed role bundles
            updateStatements.AppendLine("""
                UPDATE tenant_roles
                SET permissions = permissions || '["heartrate.read", "stepcount.read", "food.read"]'::jsonb
                WHERE slug = 'caretaker' AND is_system = true
                  AND NOT permissions @> '["heartrate.read"]';

                UPDATE tenant_roles
                SET permissions = permissions || '["heartrate.read", "stepcount.read", "food.read", "alerts.read"]'::jsonb
                WHERE slug = 'clinician' AND is_system = true
                  AND NOT permissions @> '["heartrate.read"]';

                UPDATE tenant_roles
                SET permissions = permissions || '["heartrate.read", "heartrate.readwrite", "stepcount.read", "stepcount.readwrite", "food.read", "food.readwrite", "statistics.read", "alerts.readwrite"]'::jsonb
                WHERE slug = 'admin' AND is_system = true
                  AND NOT permissions @> '["heartrate.read"]';
            """);

            // Wrap in tenant loop for RLS compliance
            migrationBuilder.Sql($"""
                DO $$
                DECLARE t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);
                        {updateStatements}
                    END LOOP;
                END $$;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse atom renames
            var reverseRenames = new (string Old, string New)[]
            {
                ("glucose.read", "entries.read"),
                ("glucose.readwrite", "entries.readwrite"),
                ("devices.read", "devicestatus.read"),
                ("devices.readwrite", "devicestatus.readwrite"),
                ("therapy.read", "profile.read"),
                ("therapy.readwrite", "profile.readwrite"),
                ("alerts.read", "notifications.read"),
                ("alerts.readwrite", "notifications.readwrite"),
                ("statistics.read", "health.read"),
            };

            var updateStatements = new System.Text.StringBuilder();

            foreach (var (table, column) in JsonbColumns)
            {
                foreach (var (oldAtom, newAtom) in reverseRenames)
                {
                    updateStatements.AppendLine($$"""
                        UPDATE {{table}}
                        SET {{column}} = (
                            SELECT jsonb_agg(
                                CASE WHEN elem #>> '{}' = '{{oldAtom}}' THEN '"{{newAtom}}"'::jsonb
                                     ELSE elem END
                            )
                            FROM jsonb_array_elements({{column}}) AS elem
                        )
                        WHERE {{column}} @> '["{{oldAtom}}"]';
                    """);
                }
            }

            foreach (var (table, column) in TextArrayColumns)
            {
                foreach (var (oldAtom, newAtom) in reverseRenames)
                {
                    updateStatements.AppendLine($$"""
                        UPDATE {{table}}
                        SET {{column}} = array_replace({{column}}, '{{oldAtom}}', '{{newAtom}}')
                        WHERE '{{oldAtom}}' = ANY({{column}});
                    """);
                }
            }

            // Remove added atoms from seed roles
            updateStatements.AppendLine("""
                UPDATE tenant_roles
                SET permissions = (
                    SELECT jsonb_agg(elem)
                    FROM jsonb_array_elements(permissions) AS elem
                    WHERE elem #>> '{}' NOT IN ('heartrate.read', 'heartrate.readwrite', 'stepcount.read', 'stepcount.readwrite', 'food.read', 'food.readwrite', 'statistics.read', 'alerts.readwrite')
                )
                WHERE slug IN ('caretaker', 'clinician', 'admin') AND is_system = true;
            """);

            // Reverse seed role slug/name renames
            updateStatements.AppendLine("""
                UPDATE tenant_roles SET slug = 'follower', name = 'Follower'
                WHERE slug = 'viewer' AND is_system = true;

                UPDATE tenant_roles SET slug = 'readable', name = 'Readable'
                WHERE slug = 'clinician' AND is_system = true;
            """);

            migrationBuilder.Sql($"""
                DO $$
                DECLARE t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);
                        {updateStatements}
                    END LOOP;
                END $$;
            """);
        }
    }
}
