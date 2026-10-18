using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Rewrites stored APS snapshots into the shape <c>ApsSnapshotCurves</c> in the API writes and
    /// reads: curves the <c>predicted_*</c> columns hold leave the command blobs, Loop's copies of
    /// <c>loop.recommended</c>, <c>loop.enacted</c> and <c>loop.predicted.values</c> go, and
    /// null-valued keys go. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC;
    /// each tenant commits on its own (which resets the GUC), so an interrupted run keeps its
    /// progress, and a rerun leaves rewritten rows as they are.
    /// </summary>
    public partial class DeduplicateApsSnapshotCurves : Migration
    {
        private const string Curves = """
            NULLIF(jsonb_strip_nulls(jsonb_build_object(
                'IOB', predicted_iob_json, 'ZT', predicted_zt_json,
                'COB', predicted_cob_json, 'UAM', predicted_uam_json)), '{}'::jsonb)
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH c AS (
                            SELECT id, {{Curves}} AS curves
                            FROM aps_snapshots
                            WHERE loop_json IS NULL AND (suggested_json ? 'predBGs' OR enacted_json ? 'predBGs'))
                        UPDATE aps_snapshots a
                        SET suggested_json = {{StripCommand("a.suggested_json")}},
                            enacted_json = {{StripCommand("a.enacted_json")}}
                        FROM c
                        WHERE c.id = a.id;

                        UPDATE aps_snapshots
                        SET suggested_json = CASE WHEN suggested_json = loop_json -> 'recommended' THEN NULL ELSE suggested_json END,
                            enacted_json = CASE WHEN enacted_json = loop_json -> 'enacted' THEN NULL ELSE enacted_json END,
                            loop_json = jsonb_strip_nulls(CASE WHEN loop_json #> '{predicted,values}' = predicted_default_json
                                                               THEN loop_json #- '{predicted,values}' ELSE loop_json END)
                        WHERE loop_json IS NOT NULL
                          AND (suggested_json IS NOT NULL OR enacted_json IS NOT NULL
                               OR loop_json #> '{predicted,values}' IS NOT NULL);

                        COMMIT;
                    END LOOP;
                END $$;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE aps_snapshots
                        SET suggested_json = CASE WHEN suggested_json ? 'predBGs' THEN suggested_json
                                                  ELSE suggested_json || jsonb_build_object('predBGs', {{Curves}}) END,
                            enacted_json = CASE WHEN enacted_json ? 'predBGs' THEN enacted_json
                                                ELSE enacted_json || jsonb_build_object('predBGs', {{Curves}}) END
                        WHERE loop_json IS NULL AND {{Curves}} IS NOT NULL;

                        UPDATE aps_snapshots
                        SET suggested_json = COALESCE(suggested_json, NULLIF(loop_json -> 'recommended', 'null'::jsonb)),
                            enacted_json = COALESCE(enacted_json, NULLIF(loop_json -> 'enacted', 'null'::jsonb)),
                            loop_json = CASE WHEN jsonb_typeof(loop_json -> 'predicted') = 'object'
                                                  AND NOT loop_json -> 'predicted' ? 'values'
                                                  AND predicted_default_json IS NOT NULL
                                             THEN jsonb_set(loop_json, '{predicted,values}', predicted_default_json)
                                             ELSE loop_json END
                        WHERE loop_json IS NOT NULL;

                        COMMIT;
                    END LOOP;
                END $$;
                """, suppressTransaction: true);
        }

        private static string StripCommand(string column) => $$"""
            CASE WHEN jsonb_strip_nulls({{column}} -> 'predBGs') = c.curves THEN jsonb_strip_nulls({{column}} - 'predBGs')
                 WHEN {{column}} -> 'predBGs' = 'null'::jsonb AND c.curves IS NOT NULL
                     THEN jsonb_strip_nulls({{column}}) || '{"predBGs": null}'::jsonb
                 ELSE jsonb_strip_nulls({{column}}) END
            """;
    }
}
