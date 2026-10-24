using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RedactSettingsValuesInAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SettingsEntity.Value and AdditionalPropertiesJson are [AuditRedacted] from here on; the
            // rows written before hold their values, which can be credentials. An update keeps the
            // record that the field changed, as the interceptor now writes it. FORCE ROW LEVEL
            // SECURITY binds the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE mutation_audit_log m
                        SET changes = (
                            SELECT jsonb_object_agg(
                                e.key,
                                CASE
                                    WHEN e.key NOT IN ('Value', 'AdditionalPropertiesJson') THEN e.value
                                    WHEN m.action = 'update' THEN '{"old": "[redacted]", "new": "[redacted]"}'::jsonb
                                    ELSE '"[redacted]"'::jsonb
                                END)
                            FROM jsonb_each(m.changes) e)
                        WHERE m.entity_type = 'Settings'
                          AND jsonb_typeof(m.changes) = 'object'
                          AND (m.changes ? 'Value' OR m.changes ? 'AdditionalPropertiesJson');
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
