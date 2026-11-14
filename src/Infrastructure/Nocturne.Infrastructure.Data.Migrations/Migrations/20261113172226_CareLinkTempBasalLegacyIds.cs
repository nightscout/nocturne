using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Gives every live CareLink temp basal the legacy id and sync key the CareLink mapper now derives
    /// from its start (<c>carelink_autobasal_</c> and the start in Unix milliseconds), so the first sync
    /// after the upgrade finds the stored rows instead of sweeping them once more. Then hard-deletes the
    /// CareLink temp basals a sync swept, with their links: each is a re-insert of a live row, written
    /// before temp basals carried a key. User deletions and other sources' rows are kept.
    /// </summary>
    /// <remarks>
    /// Two live rows with one start would share a key, so all but one is swept first, keeping the row
    /// reads show (not a duplicate copy), then the one written last, then the newest id; the sweep then
    /// removes them with the rest. A duplicate group a removed row heads, and that has members left,
    /// is headed by the copy <c>DuplicateMatching.PickSurvivor</c> would pick from those members, and each
    /// moved record's duplicate flag is set from its link. That re-head is one window pass over the
    /// tenant's temp basal links, not a lookup per group, because a tenant can hold hundreds of
    /// thousands of swept copies and a per-group plan the planner picks from a low estimate is quadratic
    /// there; the migration runs at startup. The source and key are written literally
    /// because a migration must not follow later changes to the mapper. <c>Down</c> restores nothing:
    /// the deleted rows were copies.
    /// </remarks>
    public partial class CareLinkTempBasalLegacyIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    relinked_ids uuid[];
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE temp_basals tb
                        SET deleted_at = now(), deleted_by_user = false
                        FROM (
                            SELECT id,
                                   row_number() OVER (
                                       PARTITION BY tenant_id, floor(extract(epoch FROM start_timestamp) * 1000)
                                       ORDER BY is_duplicate, sys_updated_at DESC, id DESC) AS rn
                            FROM temp_basals
                            WHERE tenant_id = t.id AND data_source = 'carelink-connector' AND deleted_at IS NULL
                        ) ranked
                        WHERE tb.id = ranked.id AND ranked.rn > 1;

                        UPDATE temp_basals tb
                        SET legacy_id = keyed.key, sync_identifier = keyed.key
                        FROM (
                            SELECT id,
                                   'carelink_autobasal_' || floor(extract(epoch FROM start_timestamp) * 1000)::bigint AS key
                            FROM temp_basals
                            WHERE tenant_id = t.id AND data_source = 'carelink-connector' AND deleted_at IS NULL
                        ) keyed
                        WHERE tb.id = keyed.id
                          AND (tb.legacy_id IS DISTINCT FROM keyed.key OR tb.sync_identifier IS DISTINCT FROM keyed.key);

                        WITH links AS (
                            SELECT id, swept,
                                   bool_or(is_primary AND swept) OVER (PARTITION BY canonical_id) AS headed_by_swept,
                                   row_number() OVER (
                                       PARTITION BY canonical_id
                                       ORDER BY swept, live DESC, source_timestamp, record_id) AS pick
                            FROM (
                                SELECT lr.id, lr.record_id, lr.canonical_id, lr.is_primary, lr.source_timestamp,
                                       coalesce(x.data_source = 'carelink-connector'
                                                AND x.deleted_at IS NOT NULL AND NOT x.deleted_by_user, false) AS swept,
                                       x.id IS NOT NULL AND x.deleted_at IS NULL AS live
                                FROM linked_records lr
                                LEFT JOIN temp_basals x ON x.id = lr.record_id
                                WHERE lr.tenant_id = t.id AND lr.record_type = 'tempbasal'
                            ) joined
                        ),
                        relinked AS (
                            UPDATE linked_records lr
                            SET is_primary = (l.pick = 1)
                            FROM links l
                            WHERE lr.id = l.id
                              AND l.headed_by_swept AND NOT l.swept
                              AND lr.is_primary <> (l.pick = 1)
                            RETURNING lr.record_id
                        )
                        SELECT coalesce(array_agg(record_id), '{}') INTO relinked_ids FROM relinked;

                        UPDATE temp_basals tb
                        SET is_duplicate = NOT tb.is_duplicate
                        FROM unnest(relinked_ids) AS relinked(id)
                        WHERE tb.tenant_id = t.id
                          AND tb.id = relinked.id
                          AND tb.is_duplicate <> EXISTS (
                              SELECT 1 FROM linked_records lr
                              WHERE lr.tenant_id = t.id
                                AND lr.record_type = 'tempbasal'
                                AND lr.record_id = tb.id
                                AND NOT lr.is_primary);

                        DELETE FROM linked_records lr
                        USING temp_basals tb
                        WHERE lr.tenant_id = t.id AND lr.record_type = 'tempbasal' AND lr.record_id = tb.id
                          AND tb.tenant_id = t.id AND tb.data_source = 'carelink-connector'
                          AND tb.deleted_at IS NOT NULL AND NOT tb.deleted_by_user;

                        DELETE FROM temp_basals
                        WHERE tenant_id = t.id AND data_source = 'carelink-connector'
                          AND deleted_at IS NOT NULL AND NOT deleted_by_user;
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
