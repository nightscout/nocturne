using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Sends back to TempBasalOriginCorroborator the temp basals it settled without judging a temp
    /// against another device's loop, or without counting a Manual default from an uploader that is
    /// not a closed loop as a default: every <c>Uncorroborated</c> row goes back to <c>Uploader</c>,
    /// and a <c>Reported</c> Manual row with no <c>automatic</c> flag and no snapshot link, written
    /// by the decomposer, from neither a closed-loop uploader nor a listed connector, goes back to no
    /// evidence, to be classified again.
    /// </summary>
    /// <remarks>
    /// A writer that states an origin keeps the <c>automatic</c> flag beside it, except the decomposer
    /// for an edit echoing a stored origin back, so such a row may have been classified from an
    /// origin stored with no evidence. Rows the decomposer writes always carry a legacy id, and a V4
    /// client's rows never do, so a Manual a V4 client stated with no flag is left out. The
    /// classification marked a closed-loop uploader's row Uploader, so one marked Reported is a
    /// statement. The uploaders and connectors listed are those of TempBasalOriginProvenance, and a
    /// flag that is not a boolean counts as none, as <c>bool.TryParse</c> reads it there. Soft-deleted
    /// rows are repaired too, so that a row, should it be restored, does not return with the evidence
    /// it was deleted with. A row is judged the same again if nothing changed, so running this twice changes nothing more,
    /// and <c>sys_updated_at</c> is left alone. FORCE ROW LEVEL SECURITY binds the migrator, hence the
    /// per-tenant GUC.
    /// </remarks>
    public partial class RepairTempBasalOriginEvidence : Migration
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

                        UPDATE temp_basals SET origin_evidence = 'Uploader'
                        WHERE tenant_id = t.id AND origin_evidence = 'Uncorroborated';

                        UPDATE temp_basals SET origin_evidence = NULL
                        WHERE tenant_id = t.id AND origin_evidence = 'Reported'
                          AND origin = 'Manual' AND aps_snapshot_id IS NULL AND legacy_id IS NOT NULL
                          AND coalesce(lower(btrim(additional_properties ->> 'automatic')), '') NOT IN ('true', 'false')
                          AND lower(coalesce(app, '')) NOT IN ('trio', 'iaps', 'freeaps-x')
                          AND lower(coalesce(app, '')) NOT LIKE 'openaps://%'
                          AND lower(coalesce(app, '')) NOT LIKE 'loop://%'
                          AND lower(coalesce(data_source, '')) NOT IN (
                              'carelink-connector', 'minimed-connector', 'glooko-connector',
                              'mylife-connector', 'tconnectsync-connector', 'twiist-connector');
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        /// <remarks>The evidence it cleared was wrong, so it is not restored.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
