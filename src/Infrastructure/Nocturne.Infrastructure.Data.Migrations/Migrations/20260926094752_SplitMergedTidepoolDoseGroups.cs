using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitMergedTidepoolDoseGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A group takes at most one Tidepool record of each type except carbs, the one type
            // Tidepool reports twice, and sensor glucose and state spans, which may repeat per
            // source. Each extra link becomes its own group, stamped past the tenant's reconcile
            // cursor and newest link so reconcile visits it and can join it to another source's copy
            // of that dose. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC loop.
            // Each statement still names the tenant, because a migrator that bypasses RLS would
            // otherwise see every tenant's rows and the scalar subqueries would fail.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY record_type, canonical_id
                                                      ORDER BY is_primary DESC, source_timestamp, record_id) AS rn
                            FROM linked_records
                            WHERE tenant_id = t.id
                              AND lower(data_source) = 'tidepool-connector'
                              AND record_type NOT IN ('carbintake', 'sensorglucose', 'statespan'))
                        UPDATE linked_records l
                        SET canonical_id = gen_random_uuid(),
                            is_primary = true,
                            sys_created_at = greatest(
                                now(),
                                (SELECT max(sys_created_at) FROM linked_records WHERE tenant_id = t.id) + interval '1 microsecond',
                                (SELECT last_reconciled_link_created_at FROM dedup_reconcile_state WHERE tenant_id = t.id) + interval '1 microsecond')
                        FROM ranked
                        WHERE ranked.id = l.id AND ranked.rn > 1 AND l.tenant_id = t.id;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-merging would hide a real dose again.
        }
    }
}
