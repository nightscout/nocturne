using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateStatisticsReadIntoReportsRead : Migration
    {
        // statistics.read was a duplicate of reports.read: it gated no endpoint and every seed role
        // that granted it also granted reports.read. Fold statistics.read into reports.read across
        // stored permissions, de-duplicating so a row that held both ends up with a single
        // reports.read. Idempotent (only rows still holding statistics.read are touched).
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
            var statements = new System.Text.StringBuilder();

            foreach (var (table, column) in JsonbColumns)
            {
                statements.AppendLine($$"""
                    UPDATE {{table}}
                    SET {{column}} = (
                        SELECT jsonb_agg(DISTINCT mapped)
                        FROM (
                            SELECT CASE WHEN elem #>> '{}' = 'statistics.read'
                                        THEN '"reports.read"'::jsonb ELSE elem END AS mapped
                            FROM jsonb_array_elements({{column}}) AS elem
                        ) m
                    )
                    WHERE {{column}} @> '["statistics.read"]';
                """);
            }

            foreach (var (table, column) in TextArrayColumns)
            {
                statements.AppendLine($$"""
                    UPDATE {{table}}
                    SET {{column}} = (
                        SELECT array_agg(DISTINCT x)
                        FROM unnest(array_replace({{column}}, 'statistics.read', 'reports.read')) AS x
                    )
                    WHERE 'statistics.read' = ANY({{column}});
                """);
            }

            // FORCE RLS applies to the migrator too, so set the tenant context per tenant.
            migrationBuilder.Sql($"""
                DO $$
                DECLARE t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);
                        {statements}
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: statistics.read and reports.read are merged into reports.read, so the
            // original split cannot be reconstructed. No-op — the schema is unchanged.
        }
    }
}
