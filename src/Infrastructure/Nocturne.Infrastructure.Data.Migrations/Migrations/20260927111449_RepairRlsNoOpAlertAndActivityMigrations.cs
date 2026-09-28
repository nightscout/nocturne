using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Repairs what two data migrations left behind by running with no tenant context, under which
    /// FORCE ROW LEVEL SECURITY hides every row from the migrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RipOutSchedulesAndEscalationSteps never resolved its <c>escalating</c> alert instances. The
    /// orchestrator only knows triggered, acknowledged and resolved, so such a row is never closed.
    /// Its <c>DELETE FROM alert_invites</c> is not redone: it only cleared invites pointing at the
    /// escalation steps it dropped, and the invites there today point at live channels.
    /// </para>
    /// <para>
    /// MigrateHeartRateStepCountToTimestamp never backfilled <c>timestamp</c> from <c>mills</c>, then
    /// dropped <c>mills</c>. Its <c>AlterColumn</c> omits <c>oldNullable</c>, so no <c>SET NOT NULL</c>
    /// was emitted and every earlier row kept a NULL timestamp. Nothing can restore the time, and a
    /// NULL sorts first under the descending timestamp reads, where materialising it into the
    /// non-nullable property throws; those rows are soft-deleted.
    /// </para>
    /// </remarks>
    public partial class RepairRlsNoOpAlertAndActivityMigrations : Migration
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

                        UPDATE alert_instances
                           SET status = 'resolved',
                               resolved_at = NOW()
                         WHERE tenant_id = t_id
                           AND status = 'escalating';

                        UPDATE heart_rates
                           SET deleted_at = NOW()
                         WHERE tenant_id = t_id
                           AND "timestamp" IS NULL
                           AND deleted_at IS NULL;

                        UPDATE step_counts
                           SET deleted_at = NOW()
                         WHERE tenant_id = t_id
                           AND "timestamp" IS NULL
                           AND deleted_at IS NULL;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: neither 'escalating' nor a NULL timestamp is a value the model can read back.
        }
    }
}
