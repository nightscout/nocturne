using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Moves the profile versions decomposed from a document posted without <c>mills</c> from the epoch,
    /// where <c>ProfileDecomposer</c> put them before it fell back to <c>startDate</c> (#2377), to the
    /// <c>startDate</c> their therapy settings row recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A live document row (a legacy id, not a profile-switch snapshot) at the epoch moves with its four
    /// schedules: the live rows of the same legacy id that are still at the epoch. A store's five rows
    /// keep one <c>timestamp</c> and take one <c>sys_updated_at</c>, so the same-batch witness of the
    /// mmol repairs holds whichever of them runs first, and a v3 history client fetches the moved
    /// document. Zone-less dates read as UTC, as the decomposer reads them.
    /// </para>
    /// <para>
    /// The document's <c>created_at</c>, the decomposer's last fallback, was never stored. A row whose
    /// <c>start_date</c> is missing or does not parse is left at the epoch and named in a notice; a
    /// re-upload of its document moves it. A moved row is no longer selected, so running this again
    /// changes nothing. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC.
    /// </para>
    /// <para>
    /// The percent temp basal rates resolved against these versions are re-resolved after startup by
    /// <c>TempBasalRateSweepService</c>, which SQL cannot do. The default flag is not moved: every
    /// upload at the epoch claimed it, so it sits on the last document uploaded.
    /// </para>
    /// </remarks>
    public partial class RepairEpochProfileVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RepairSql);
        }

        /// <inheritdoc />
        /// <remarks>The timestamps it replaced were wrong, so they are not restored.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }

        private const string RepairSql = """
            DO $$
            DECLARE
                epoch CONSTANT timestamptz := 'epoch';
                t RECORD;
                r RECORD;
                start_at timestamptz;
                n_rows int;
                n_stores int;
                n_schedules int;
                n_left int;
            BEGIN
                PERFORM set_config('timezone', 'UTC', true);

                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);
                    n_stores := 0; n_schedules := 0; n_left := 0;

                    FOR r IN
                        SELECT ts.id, ts.legacy_id, ts.start_date
                        FROM therapy_settings ts
                        WHERE ts.tenant_id = t.id
                          AND ts.deleted_at IS NULL
                          AND ts.timestamp = epoch
                          AND ts.legacy_id IS NOT NULL
                          AND strpos(ts.profile_name, '@@@@@') = 0
                    LOOP
                        BEGIN
                            start_at := NULLIF(btrim(r.start_date), '')::timestamptz;
                        EXCEPTION WHEN data_exception THEN
                            start_at := NULL;
                        END;

                        IF start_at IS NULL OR start_at = epoch THEN
                            n_left := n_left + 1;
                            RAISE NOTICE 'tenant %: therapy_settings % (%) left at the epoch: no startDate to move it to (%)',
                                t.id, r.id, r.legacy_id, coalesce(r.start_date, 'none');
                            CONTINUE;
                        END IF;

                        UPDATE therapy_settings SET timestamp = start_at, sys_updated_at = now()
                        WHERE id = r.id AND tenant_id = t.id;

                        UPDATE basal_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        UPDATE carb_ratio_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        UPDATE sensitivity_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        UPDATE target_range_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        n_stores := n_stores + 1;
                    END LOOP;

                    IF n_stores + n_left > 0 THEN
                        RAISE NOTICE 'tenant %: moved % profile stores and % of their schedules off the epoch to their startDate; left % at the epoch',
                            t.id, n_stores, n_schedules, n_left;
                    END IF;
                END LOOP;
            END $$;
            """;
    }
}
