using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Gives the Override spans stored before Override metadata carried a <c>collection</c> key the
    /// <c>"treatments"</c> key the legacy treatment reads select on. Only the treatment decomposer
    /// ever wrote <c>utcOffset</c>, so a keyless Override span with an original id and a
    /// <c>utcOffset</c> is a treatment's. FORCE ROW LEVEL SECURITY binds the migrator, hence the
    /// per-tenant GUC; the tenant is named in the statement too, so a role that bypasses RLS still
    /// updates each tenant once. <c>updated_at</c> moves so a v3 history client whose cursor has
    /// passed these rows receives them, spread 1000 rows to a millisecond as
    /// <c>NocturneDbContext.SystemTimestampGroupSize</c> requires.
    /// </summary>
    public partial class BackfillOverrideCollectionKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                              AND category = 'Override'
                              AND original_id IS NOT NULL
                              AND NOT metadata ? 'collection'
                              AND metadata ? 'utcOffset')
                        UPDATE state_spans s
                        SET metadata = s.metadata || '{"collection": "treatments"}'::jsonb,
                            updated_at = now() + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = s.id;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        /// <remarks>
        /// A no-op: a backfilled key cannot be told from one the treatment decomposer wrote, and the
        /// key is what the current code expects either way.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
