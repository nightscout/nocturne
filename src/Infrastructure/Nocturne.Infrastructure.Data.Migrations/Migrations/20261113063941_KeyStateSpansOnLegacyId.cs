using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Makes <c>original_id</c> the unique re-import key of <c>state_spans</c>, as <c>legacy_id</c> is on
    /// the V4 record tables: one live row per tenant and id, with the user-tombstone companion the
    /// re-import guard reads and an unfiltered index for lookups of deleted spans. Every loser of a
    /// live duplicate is soft-deleted first, as a system delete so its id stays re-importable. The
    /// winner is the row reads show (not a duplicate copy), then the one written last, then the newest
    /// id.
    /// </summary>
    /// <remarks>
    /// A soft delete fires no foreign key action, so the cleanup first does what the repository's own
    /// state-span delete does. A live span superseded by a loser is superseded by its winner, the same
    /// span, where <c>StateSpanSupersessionExtensions.ClearSupersededByAsync</c> would clear it. A
    /// duplicate group a loser heads gets the primary <c>DuplicateGroupPrimaries.RepointAwayFromAsync</c>
    /// would pick, with the winner first, and each moved record's duplicate flag is set from its link.
    /// </remarks>
    public partial class KeyStateSpansOnLegacyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    loser_ids uuid[];
                    winner_ids uuid[];
                    relinked_ids uuid[];
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        SELECT coalesce(array_agg(ranked.id ORDER BY ranked.id), '{}'),
                               coalesce(array_agg(ranked.winner_id ORDER BY ranked.id), '{}')
                        INTO loser_ids, winner_ids
                        FROM (
                            SELECT id,
                                   row_number() OVER (
                                       PARTITION BY tenant_id, original_id
                                       ORDER BY is_duplicate, updated_at DESC, id DESC) AS rn,
                                   first_value(id) OVER (
                                       PARTITION BY tenant_id, original_id
                                       ORDER BY is_duplicate, updated_at DESC, id DESC) AS winner_id
                            FROM state_spans
                            WHERE tenant_id = t.id AND original_id IS NOT NULL AND deleted_at IS NULL
                        ) ranked
                        WHERE ranked.rn > 1;

                        CONTINUE WHEN cardinality(loser_ids) = 0;

                        UPDATE state_spans s
                        SET superseded_by_id = NULLIF(l.winner_id, s.id), updated_at = now()
                        FROM unnest(loser_ids, winner_ids) AS l(id, winner_id)
                        WHERE s.tenant_id = t.id
                          AND s.deleted_at IS NULL
                          AND s.superseded_by_id = l.id
                          AND NOT s.id = ANY (loser_ids);

                        WITH headed AS (
                            SELECT DISTINCT lr.canonical_id, l.winner_id
                            FROM linked_records lr
                            JOIN unnest(loser_ids, winner_ids) AS l(id, winner_id) ON lr.record_id = l.id
                            WHERE lr.tenant_id = t.id AND lr.record_type = 'statespan' AND lr.is_primary
                        ),
                        members AS (
                            SELECT h.canonical_id, h.winner_id, m.record_id, m.source_timestamp,
                                   s.id IS NOT NULL AS live, s.superseded_by_id, s.end_timestamp
                            FROM headed h
                            JOIN linked_records m
                              ON m.tenant_id = t.id AND m.record_type = 'statespan' AND m.canonical_id = h.canonical_id
                            LEFT JOIN state_spans s
                              ON s.id = m.record_id AND s.deleted_at IS NULL AND NOT s.id = ANY (loser_ids)
                        ),
                        picked AS (
                            SELECT DISTINCT ON (m.canonical_id) m.canonical_id, m.record_id
                            FROM members m
                            ORDER BY m.canonical_id,
                                     (m.live AND m.record_id = m.winner_id) DESC,
                                     CASE
                                         WHEN NOT m.live THEN 3
                                         WHEN EXISTS (
                                             SELECT 1 FROM members sup
                                             WHERE sup.canonical_id = m.canonical_id
                                               AND sup.live
                                               AND sup.record_id = m.superseded_by_id) THEN 2
                                         WHEN m.end_timestamp IS NOT NULL THEN 1
                                         ELSE 0
                                     END,
                                     m.source_timestamp,
                                     m.record_id
                        ),
                        relinked AS (
                            UPDATE linked_records lr
                            SET is_primary = (lr.record_id = p.record_id)
                            FROM picked p
                            WHERE lr.tenant_id = t.id
                              AND lr.record_type = 'statespan'
                              AND lr.canonical_id = p.canonical_id
                              AND lr.is_primary <> (lr.record_id = p.record_id)
                            RETURNING lr.record_id
                        )
                        SELECT coalesce(array_agg(record_id), '{}') INTO relinked_ids FROM relinked;

                        UPDATE state_spans s
                        SET is_duplicate = NOT s.is_duplicate
                        WHERE s.tenant_id = t.id
                          AND s.id = ANY (relinked_ids)
                          AND s.is_duplicate <> EXISTS (
                              SELECT 1 FROM linked_records lr
                              WHERE lr.tenant_id = t.id
                                AND lr.record_type = 'statespan'
                                AND lr.record_id = s.id
                                AND NOT lr.is_primary);

                        UPDATE state_spans
                        SET deleted_at = now(), deleted_by_user = false
                        WHERE tenant_id = t.id AND id = ANY (loser_ids);
                    END LOOP;
                END $$;
                """);

            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_state_spans_tenant_legacy_id",
                "ON state_spans (tenant_id, original_id) WHERE original_id IS NOT NULL AND deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_state_spans_tenant_legacy_id_user_deleted",
                "ON state_spans (tenant_id, original_id) WHERE original_id IS NOT NULL AND deleted_by_user");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_state_spans_tenant_legacy_id_any",
                "ON state_spans (tenant_id, original_id)");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_original_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(migrationBuilder, "ix_state_spans_original_id", "ON state_spans (original_id)");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_legacy_id_any");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_legacy_id_user_deleted");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_legacy_id");
        }
    }
}
