using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Services;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Marks the move to the stored shape <c>ApsSnapshotCurves</c> in the API writes and reads. Up
    /// changes no rows: the API reads both shapes, and <see cref="ApsSnapshotCurvesRewriter"/>
    /// rewrites stored rows a page at a time after startup, since one statement over a large table
    /// can outlast the migration command timeout. Down returns every row to the shape the code
    /// before it reads, in one pass per tenant.
    /// </summary>
    public partial class DeduplicateApsSnapshotCurves : Migration
    {
        private const string Curves = ApsSnapshotCurvesRewriter.Curves;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
    }
}
