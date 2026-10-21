using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Converts to mg/dL the targets and ISF a mmol profile's V4 schedules hold in mmol: before #521,
    /// <c>ProfileDecomposer</c> stored them as uploaded (target 5.5, ISF 3.5), and every reader now takes
    /// them as mg/dL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Targets. A live target schedule is raw when a live owner's units are mmol and every low and high
    /// target is a number from 2.0 to 22.2. No mg/dL target is that low: AAPS's profile targets start at
    /// 80 mg/dL, Loop's correction range at 87, and one conversion of a 2.0 mmol/L target stores 36. Each
    /// value becomes <c>round(v * 18.0182)</c>, the decomposer's conversion, which lands at 36 to 400 and
    /// is never selected again.
    /// </para>
    /// <para>
    /// ISF. A value alone cannot prove a raw ISF: AAPS accepts an ISF down to 2 mg/dL/U. So an ISF is
    /// converted only when all of these hold:
    /// </para>
    /// <list type="bullet">
    /// <item>It is the live ISF schedule of a converted target schedule's store (same legacy id, or same
    /// correlation and profile name), with the same <c>timestamp</c> and a <c>sys_updated_at</c> within a
    /// minute of the targets': the decomposer, which stored ISF as uploaded too, wrote both in one batch,
    /// and no V4 edit of either came after.</item>
    /// <item>Every value is a number of at least 0.5 and under 9. Under 9 mg/dL/U is below Loop's (10) and
    /// Trio's (9) lowest ISF, from a client that sent its targets in mmol; 0.5 mmol/L/U is 9 mg/dL/U.</item>
    /// </list>
    /// <para>
    /// Any other ISF beside converted targets is left and listed for review by hand. Leaving a raw ISF
    /// errs towards corrections 18 times too strong, converting a correct one towards 18 times too weak,
    /// so only the band above is converted. A profile-activation snapshot is one write, so its own targets
    /// are its ISF's batch; no known path built one in mmol, as migration <c>AddProfileActivations</c> and
    /// <c>ProfileActivationSnapshots</c> both convert. Its <c>profileJson</c> is served as uploaded and is
    /// left alone.
    /// </para>
    /// <para>
    /// Each row is converted in its own block, so a value PostgreSQL cannot read leaves that row with a
    /// notice rather than failing the migration. Soft-deleted rows are not touched, converted rows take a
    /// new <c>sys_updated_at</c> for v3 history clients, and a second run selects nothing. Notices list
    /// every row left, and every live mmol schedule or snapshot still under the bounds afterwards. FORCE
    /// ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC.
    /// </para>
    /// </remarks>
    public partial class RepairRawMmolProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RepairSql);
        }

        /// <inheritdoc />
        /// <remarks>The values it replaced were wrong, so they are not restored.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }

        private const string RawTarget =
            "CASE WHEN jsonb_typeof(e->'Low') = 'number' AND jsonb_typeof(e->'High') = 'number' "
            + "THEN (e->'Low')::numeric BETWEEN 2.0 AND 22.2 AND (e->'High')::numeric BETWEEN 2.0 AND 22.2 ELSE false END";

        private const string AnyRawTarget =
            "(CASE WHEN jsonb_typeof(e->'Low') = 'number' THEN (e->'Low')::numeric <= 22.2 ELSE false END "
            + "OR CASE WHEN jsonb_typeof(e->'High') = 'number' THEN (e->'High')::numeric <= 22.2 ELSE false END)";

        private const string RawIsf =
            "CASE WHEN jsonb_typeof(e->'Value') = 'number' THEN (e->'Value')::numeric >= 0.5 AND (e->'Value')::numeric < 9 ELSE false END";

        private const string AnyRawIsf =
            "CASE WHEN jsonb_typeof(e->'Value') = 'number' THEN (e->'Value')::numeric < 9 ELSE false END";

        private static string AsArray(string json) =>
            $"CASE WHEN jsonb_typeof({json}) = 'array' THEN {json} ELSE '[]'::jsonb END";

        private static string MmolOwner(string alias) => $"""
            EXISTS (
                SELECT 1 FROM therapy_settings ts
                WHERE ts.tenant_id = t.id
                  AND ts.deleted_at IS NULL
                  AND lower(ts.units) IN ('mmol', 'mmol/l')
                  AND (ts.legacy_id = {alias}.legacy_id
                       OR (ts.correlation_id = {alias}.correlation_id AND ts.profile_name = {alias}.profile_name)))
            """;

        private static readonly string RepairSql = $$"""
            DO $$
            DECLARE
                f CONSTANT numeric := 18.0182;
                t RECORD;
                ids uuid[];
                isf_ids uuid[];
                rid uuid;
                sid uuid;
                repaired_isf uuid[];
                left_isf uuid[];
                left_snapshots uuid[];
                r_legacy text;
                r_correlation uuid;
                r_profile text;
                r_timestamp timestamptz;
                r_updated timestamptz;
                s_timestamp timestamptz;
                s_updated timestamptz;
                entries jsonb;
                isf jsonb;
                snap jsonb;
                left_over text;
                n_targets int;
                n_isf int;
                n_snapshots int;
                n_skipped int;
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);
                    n_targets := 0; n_isf := 0; n_snapshots := 0; n_skipped := 0;
                    repaired_isf := ARRAY[]::uuid[];
                    left_isf := ARRAY[]::uuid[];
                    left_snapshots := ARRAY[]::uuid[];

                    SELECT array_agg(tr.id) INTO ids
                    FROM target_range_schedules tr
                    WHERE tr.tenant_id = t.id
                      AND tr.deleted_at IS NULL
                      AND {{MmolOwner("tr")}}
                      AND (SELECT bool_and({{RawTarget}})
                           FROM jsonb_array_elements({{AsArray("tr.entries_json")}}) e);

                    FOREACH rid IN ARRAY COALESCE(ids, ARRAY[]::uuid[]) LOOP
                        BEGIN
                            SELECT tr.legacy_id, tr.correlation_id, tr.profile_name, tr.timestamp, tr.sys_updated_at, tr.entries_json
                            INTO r_legacy, r_correlation, r_profile, r_timestamp, r_updated, entries
                            FROM target_range_schedules tr WHERE tr.id = rid AND tr.tenant_id = t.id;

                            entries := (SELECT jsonb_agg(e || jsonb_build_object(
                                                'Low', round((e->'Low')::numeric * f),
                                                'High', round((e->'High')::numeric * f)) ORDER BY o)
                                        FROM jsonb_array_elements(entries) WITH ORDINALITY x(e, o));

                            UPDATE target_range_schedules SET entries_json = entries, sys_updated_at = now()
                            WHERE id = rid AND tenant_id = t.id;
                            n_targets := n_targets + 1;
                        EXCEPTION WHEN data_exception THEN
                            n_skipped := n_skipped + 1;
                            RAISE NOTICE 'tenant %: target_range_schedules % left as it is: %', t.id, rid, SQLERRM;
                            CONTINUE;
                        END;

                        SELECT array_agg(si.id) INTO isf_ids
                        FROM sensitivity_schedules si
                        WHERE si.tenant_id = t.id
                          AND si.deleted_at IS NULL
                          AND (si.legacy_id = r_legacy
                               OR (si.correlation_id = r_correlation AND si.profile_name = r_profile))
                          AND NOT si.id = ANY(repaired_isf);

                        FOREACH sid IN ARRAY COALESCE(isf_ids, ARRAY[]::uuid[]) LOOP
                            BEGIN
                                SELECT si.entries_json, si.timestamp, si.sys_updated_at
                                INTO isf, s_timestamp, s_updated
                                FROM sensitivity_schedules si WHERE si.id = sid AND si.tenant_id = t.id;

                                IF s_timestamp IS DISTINCT FROM r_timestamp
                                   OR abs(extract(epoch FROM s_updated - r_updated)) > 60 THEN
                                    n_skipped := n_skipped + 1;
                                    left_isf := left_isf || sid;
                                    RAISE NOTICE 'tenant %: sensitivity_schedules % left as it is: not written with target_range_schedules %', t.id, sid, rid;
                                    CONTINUE;
                                END IF;

                                IF NOT COALESCE((SELECT bool_and({{RawIsf}}) FROM jsonb_array_elements({{AsArray("isf")}}) e), false) THEN
                                    n_skipped := n_skipped + 1;
                                    left_isf := left_isf || sid;
                                    RAISE NOTICE 'tenant %: sensitivity_schedules % left as it is: not every value is at least 0.5 and under 9, so it could be a correct mg/dL/U ISF', t.id, sid;
                                    CONTINUE;
                                END IF;

                                isf := (SELECT jsonb_agg(e || jsonb_build_object('Value', round((e->'Value')::numeric * f)) ORDER BY o)
                                        FROM jsonb_array_elements(isf) WITH ORDINALITY x(e, o));

                                UPDATE sensitivity_schedules SET entries_json = isf, sys_updated_at = now()
                                WHERE id = sid AND tenant_id = t.id;
                                repaired_isf := repaired_isf || sid;
                                n_isf := n_isf + 1;
                            EXCEPTION WHEN data_exception THEN
                                n_skipped := n_skipped + 1;
                                left_isf := left_isf || sid;
                                RAISE NOTICE 'tenant %: sensitivity_schedules % left as it is: %', t.id, sid, SQLERRM;
                            END;
                        END LOOP;
                    END LOOP;

                    SELECT array_agg(p.id) INTO ids
                    FROM profile_activations p
                    WHERE p.tenant_id = t.id
                      AND p.deleted_at IS NULL
                      AND lower(p.snapshot->>'Units') IN ('mmol', 'mmol/l')
                      AND (SELECT bool_and({{RawTarget}})
                           FROM jsonb_array_elements({{AsArray("p.snapshot->'Targets'")}}) e);

                    FOREACH rid IN ARRAY COALESCE(ids, ARRAY[]::uuid[]) LOOP
                        BEGIN
                            SELECT p.snapshot INTO snap FROM profile_activations p WHERE p.id = rid AND p.tenant_id = t.id;

                            entries := (SELECT jsonb_agg(e || jsonb_build_object(
                                                'Low', round((e->'Low')::numeric * f),
                                                'High', round((e->'High')::numeric * f)) ORDER BY o)
                                        FROM jsonb_array_elements(snap->'Targets') WITH ORDINALITY x(e, o));
                            snap := jsonb_set(snap, '{Targets}', entries);

                            isf := snap->'Sensitivity';
                            IF COALESCE((SELECT bool_and({{RawIsf}}) FROM jsonb_array_elements({{AsArray("isf")}}) e), false) THEN
                                snap := jsonb_set(snap, '{Sensitivity}',
                                    (SELECT jsonb_agg(e || jsonb_build_object('Value', round((e->'Value')::numeric * f)) ORDER BY o)
                                     FROM jsonb_array_elements(isf) WITH ORDINALITY x(e, o)));
                            ELSIF jsonb_array_length({{AsArray("isf")}}) > 0 THEN
                                n_skipped := n_skipped + 1;
                                left_snapshots := left_snapshots || rid;
                                RAISE NOTICE 'tenant %: profile_activations % sensitivity left as it is: not every value is at least 0.5 and under 9, so it could be a correct mg/dL/U ISF', t.id, rid;
                            END IF;

                            UPDATE profile_activations SET snapshot = snap, sys_updated_at = now()
                            WHERE id = rid AND tenant_id = t.id;
                            n_snapshots := n_snapshots + 1;
                        EXCEPTION WHEN data_exception THEN
                            n_skipped := n_skipped + 1;
                            RAISE NOTICE 'tenant %: profile_activations % left as it is: %', t.id, rid, SQLERRM;
                        END;
                    END LOOP;

                    IF n_targets + n_isf + n_snapshots + n_skipped > 0 THEN
                        RAISE NOTICE 'tenant %: converted % target range schedules, % sensitivity schedules, % activation snapshots from mmol; left % as they were',
                            t.id, n_targets, n_isf, n_snapshots, n_skipped;
                    END IF;

                    left_isf := ARRAY(SELECT DISTINCT x FROM unnest(left_isf) x WHERE NOT x = ANY(repaired_isf));
                    IF cardinality(left_isf) > 0 THEN
                        RAISE NOTICE 'tenant %: sensitivity_schedules left beside converted targets, to review by hand: %',
                            t.id, array_to_string(left_isf, ', ');
                    END IF;

                    IF cardinality(left_snapshots) > 0 THEN
                        RAISE NOTICE 'tenant %: profile_activations whose sensitivity was left beside converted targets, to review by hand: %',
                            t.id, array_to_string(left_snapshots, ', ');
                    END IF;

                    SELECT string_agg(tr.id::text, ', ') INTO left_over
                    FROM target_range_schedules tr
                    WHERE tr.tenant_id = t.id
                      AND tr.deleted_at IS NULL
                      AND {{MmolOwner("tr")}}
                      AND EXISTS (SELECT 1 FROM jsonb_array_elements({{AsArray("tr.entries_json")}}) e WHERE {{AnyRawTarget}});
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol target_range_schedules still holding a target at or under 22.2: %', t.id, left_over;
                    END IF;

                    SELECT string_agg(si.id::text, ', ') INTO left_over
                    FROM sensitivity_schedules si
                    WHERE si.tenant_id = t.id
                      AND si.deleted_at IS NULL
                      AND {{MmolOwner("si")}}
                      AND EXISTS (SELECT 1 FROM jsonb_array_elements({{AsArray("si.entries_json")}}) e WHERE {{AnyRawIsf}});
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol sensitivity_schedules still holding an ISF under 9: %', t.id, left_over;
                    END IF;

                    SELECT string_agg(p.id::text, ', ') INTO left_over
                    FROM profile_activations p
                    WHERE p.tenant_id = t.id
                      AND p.deleted_at IS NULL
                      AND lower(p.snapshot->>'Units') IN ('mmol', 'mmol/l')
                      AND (EXISTS (SELECT 1 FROM jsonb_array_elements({{AsArray("p.snapshot->'Targets'")}}) e WHERE {{AnyRawTarget}})
                           OR EXISTS (SELECT 1 FROM jsonb_array_elements({{AsArray("p.snapshot->'Sensitivity'")}}) e WHERE {{AnyRawIsf}}));
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol profile_activations snapshots still holding a target at or under 22.2 or an ISF under 9: %', t.id, left_over;
                    END IF;
                END LOOP;
            END $$;
            """;
    }
}
