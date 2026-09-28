using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Redoes AlertsRedesign's <c>severity = 'normal'</c> to <c>'warning'</c> rename, which never ran:
    /// its loop read tenant ids from <c>alert_rules</c>, and FORCE ROW LEVEL SECURITY hides every row
    /// from a migrator with no tenant context. <c>AlertRuleSeverity</c> has no <c>normal</c> member, so
    /// one such row makes reading the tenant's enabled rules throw and stops all of its alert evaluation.
    /// </summary>
    /// <remarks>
    /// AlertsRedesign's condition rewrite is deliberately not redone. A leaf <c>condition_type</c> is a
    /// valid rule shape today, and the legacy leaves still evaluate: threshold and rate of change through
    /// their evaluators, signal loss through the sweep's own path, which a staleness rewrite would move
    /// it off. The confirmation and hysteresis values it would have folded in were dropped with their
    /// columns.
    /// </remarks>
    public partial class RerunAlertRuleSeverityRenameUnderTenantContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        UPDATE alert_rules
                           SET severity = 'warning'
                         WHERE tenant_id = t_id
                           AND severity = 'normal';
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: 'normal' is not a value the model can read back.
        }
    }
}
