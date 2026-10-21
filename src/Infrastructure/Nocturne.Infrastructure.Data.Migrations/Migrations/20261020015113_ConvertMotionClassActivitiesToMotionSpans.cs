using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Moves the xDrip <c>motion-class</c> activities stored as open Exercise spans to the Motion
    /// category, with the record's <c>class</c> as the state, and ends each live Motion span at the
    /// next later start in its tenant, as <c>StateSpanRepository</c> supersession would have. A span
    /// stored at the epoch, by a migration that ignored xDrip's <c>timeStamp</c>, starts at that
    /// <c>timeStamp</c> instead. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant
    /// GUC; the tenant is named in each statement too, so a role that bypasses RLS still updates each
    /// tenant once. Every row either statement changed has <c>updated_at</c> stamped once, by a last
    /// statement, 1000 rows to a millisecond as <c>NocturneDbContext.SystemTimestampGroupSize</c>
    /// requires.
    /// </summary>
    public partial class ConvertMotionClassActivitiesToMotionSpans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    converted uuid[];
                    ended uuid[];
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH changed AS (
                            UPDATE state_spans s
                            SET category = 'Motion',
                                state = coalesce(s.metadata #>> '{additionalProperties,class}', s.state),
                                start_timestamp = CASE
                                    WHEN s.start_timestamp = to_timestamp(0)
                                     AND s.metadata #>> '{additionalProperties,timeStamp}' ~ '^[1-9][0-9]{0,15}$'
                                    THEN to_timestamp((s.metadata #>> '{additionalProperties,timeStamp}')::bigint / 1000.0)
                                    ELSE s.start_timestamp
                                END
                            WHERE s.tenant_id = t.id
                              AND s.category = 'Exercise'
                              AND s.state = 'motion-class'
                            RETURNING s.id)
                        SELECT coalesce(array_agg(id), '{}') INTO converted FROM changed;

                        WITH live AS (
                            SELECT id, start_timestamp, end_timestamp
                            FROM state_spans
                            WHERE tenant_id = t.id
                              AND category = 'Motion'
                              AND deleted_at IS NULL),
                        starts AS (
                            SELECT DISTINCT ON (start_timestamp) start_timestamp, id
                            FROM live
                            ORDER BY start_timestamp, id),
                        chain AS (
                            SELECT start_timestamp,
                                   lead(start_timestamp) OVER w AS next_start,
                                   lead(id) OVER w AS next_id
                            FROM starts
                            WINDOW w AS (ORDER BY start_timestamp)),
                        changed AS (
                            UPDATE state_spans s
                            SET end_timestamp = chain.next_start,
                                superseded_by_id = chain.next_id
                            FROM live
                            JOIN chain ON chain.start_timestamp = live.start_timestamp
                            WHERE s.id = live.id
                              AND live.end_timestamp IS NULL
                              AND chain.next_start IS NOT NULL
                            RETURNING s.id)
                        SELECT coalesce(array_agg(id), '{}') INTO ended FROM changed;

                        WITH ranked AS (
                            SELECT id, row_number() OVER (ORDER BY id) - 1 AS k
                            FROM (SELECT DISTINCT unnest(converted || ended) AS id) touched)
                        UPDATE state_spans s
                        SET updated_at = now() + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE s.tenant_id = t.id
                          AND s.id = ranked.id;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Returns every Motion span to the Exercise <c>motion-class</c> span the code before Motion
        /// reads, since that code reads an unknown category as PumpMode. Ends and corrected starts stay.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id, row_number() OVER (ORDER BY id) - 1 AS k
                            FROM state_spans
                            WHERE tenant_id = t.id
                              AND category = 'Motion')
                        UPDATE state_spans s
                        SET category = 'Exercise',
                            state = 'motion-class',
                            updated_at = now() + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = s.id;
                    END LOOP;
                END $$;
                """);
        }
    }
}
