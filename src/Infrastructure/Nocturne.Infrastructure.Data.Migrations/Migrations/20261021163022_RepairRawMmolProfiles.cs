using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Converts to mg/dL the targets and ISF a mmol profile's V4 schedules hold in mmol: before #521,
    /// <c>ProfileDecomposer</c> stored both as uploaded (target 5.5, ISF 3.5), and every reader now takes
    /// them as mg/dL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A live target schedule is raw when a live owner's units are mmol and every low and high target is a
    /// number from 2.0 to 22.2. No mg/dL target is that low: AAPS's profile targets start at 80 mg/dL and
    /// Loop's correction range at 87, while one conversion of a 2.0 mmol/L target stores 36 and of 22.2
    /// stores 400.
    /// </para>
    /// <para>
    /// Its store's ISF is raw too when it was written in the same decomposer batch, which stored ISF as
    /// uploaded as well: every live ISF schedule of the store (same legacy id, or same correlation and
    /// profile name) has the targets' <c>timestamp</c> and a <c>sys_updated_at</c> within a minute of
    /// theirs, and holds only numbers from 0.5 to 22.2 mmol/L/U (9 to 400 mg/dL/U).
    /// </para>
    /// <para>
    /// A store is converted whole or not at all. Raw targets read as about 0.3 mmol/L, which AAPS rejects
    /// with the whole profile; converting them beside an ISF left raw would let that ISF through, 18 times
    /// too strong. So a raw target schedule is converted only with every ISF schedule of its store, and is
    /// left raw, and reported, when the store has no ISF schedule or any one fails the rules. An ISF
    /// schedule two raw target schedules share leaves both when either is left. A profile-activation
    /// snapshot takes the same rules with its own targets and ISF; no known path built one raw, as
    /// migration <c>AddProfileActivations</c> and <c>ProfileActivationSnapshots</c> both convert.
    /// </para>
    /// <para>
    /// Every value becomes <c>round(v * 18.0182)</c>, the decomposer's conversion, which lands above every
    /// bound, so a second run selects nothing. Each store is converted in its own block, so a value
    /// PostgreSQL cannot read leaves that store with a notice rather than failing the migration.
    /// Soft-deleted rows are not touched, converted rows take a new <c>sys_updated_at</c> for v3 history
    /// clients, and notices list every store left. FORCE ROW LEVEL SECURITY binds the migrator, hence the
    /// per-tenant GUC.
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
            "CASE WHEN jsonb_typeof(e->'Value') = 'number' THEN (e->'Value')::numeric BETWEEN 0.5 AND 22.2 ELSE false END";

        private const string AnyRawIsf =
            "CASE WHEN jsonb_typeof(e->'Value') = 'number' THEN (e->'Value')::numeric <= 22.2 ELSE false END";

        private static string AsArray(string json) =>
            $"CASE WHEN jsonb_typeof({json}) = 'array' THEN {json} ELSE '[]'::jsonb END";

        private static string AllRaw(string json, string test) =>
            $"COALESCE((SELECT bool_and({test}) FROM jsonb_array_elements({AsArray(json)}) e), false)";

        private static string Converted(string json, string fields) => $"""
            (SELECT jsonb_agg(e || jsonb_build_object({fields}) ORDER BY o)
             FROM jsonb_array_elements({json}) WITH ORDINALITY x(e, o))
            """;

        private const string TargetFields =
            "'Low', round((e->'Low')::numeric * f), 'High', round((e->'High')::numeric * f)";

        private const string IsfFields = "'Value', round((e->'Value')::numeric * f)";

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
                cand uuid[];
                pair_t uuid[];
                pair_s uuid[];
                pair_ok boolean[];
                bad uuid[];
                more uuid[];
                tainted uuid[];
                converted_isf uuid[];
                rid uuid;
                sid uuid;
                snap jsonb;
                left_over text;
                n_targets int;
                n_isf int;
                n_snapshots int;
                n_left int;
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);
                    n_targets := 0; n_isf := 0; n_snapshots := 0; n_left := 0;
                    converted_isf := ARRAY[]::uuid[];

                    cand := ARRAY(
                        SELECT tr.id FROM target_range_schedules tr
                        WHERE tr.tenant_id = t.id
                          AND tr.deleted_at IS NULL
                          AND {{MmolOwner("tr")}}
                          AND (SELECT bool_and({{RawTarget}})
                               FROM jsonb_array_elements({{AsArray("tr.entries_json")}}) e));

                    SELECT COALESCE(array_agg(tr.id), ARRAY[]::uuid[]),
                           COALESCE(array_agg(si.id), ARRAY[]::uuid[]),
                           COALESCE(array_agg(COALESCE(
                               si.timestamp = tr.timestamp
                               AND abs(extract(epoch FROM si.sys_updated_at - tr.sys_updated_at)) <= 60
                               AND {{AllRaw("si.entries_json", RawIsf)}}, false)), ARRAY[]::boolean[])
                    INTO pair_t, pair_s, pair_ok
                    FROM target_range_schedules tr
                    JOIN sensitivity_schedules si
                      ON si.tenant_id = t.id
                     AND si.deleted_at IS NULL
                     AND (si.legacy_id = tr.legacy_id
                          OR (si.correlation_id = tr.correlation_id AND si.profile_name = tr.profile_name))
                    WHERE tr.tenant_id = t.id AND tr.id = ANY(cand);

                    bad := ARRAY(
                        SELECT c FROM unnest(cand) c
                        WHERE NOT EXISTS (SELECT 1 FROM unnest(pair_t, pair_ok) p(tr_id, ok) WHERE p.tr_id = c AND p.ok)
                           OR EXISTS (SELECT 1 FROM unnest(pair_t, pair_ok) p(tr_id, ok) WHERE p.tr_id = c AND NOT p.ok));
                    LOOP
                        tainted := ARRAY(SELECT DISTINCT p.si_id FROM unnest(pair_t, pair_s) p(tr_id, si_id) WHERE p.tr_id = ANY(bad));
                        more := ARRAY(SELECT DISTINCT p.tr_id FROM unnest(pair_t, pair_s) p(tr_id, si_id)
                                      WHERE p.si_id = ANY(tainted) AND NOT p.tr_id = ANY(bad));
                        EXIT WHEN cardinality(more) = 0;
                        bad := bad || more;
                    END LOOP;

                    FOREACH rid IN ARRAY bad LOOP
                        n_left := n_left + 1;
                        RAISE NOTICE 'tenant %: target_range_schedules % left in mmol with its store: ISF schedules %, none of which may be left, are not all from its batch at 0.5 to 22.2',
                            t.id, rid,
                            COALESCE((SELECT string_agg(DISTINCT p.si_id::text, ', ') FROM unnest(pair_t, pair_s) p(tr_id, si_id) WHERE p.tr_id = rid), 'absent');
                    END LOOP;

                    FOREACH rid IN ARRAY ARRAY(SELECT c FROM unnest(cand) c WHERE NOT c = ANY(bad)) LOOP
                        BEGIN
                            UPDATE target_range_schedules
                            SET entries_json = {{Converted("entries_json", TargetFields)}}, sys_updated_at = now()
                            WHERE id = rid AND tenant_id = t.id;
                            n_targets := n_targets + 1;

                            FOREACH sid IN ARRAY ARRAY(
                                SELECT DISTINCT p.si_id FROM unnest(pair_t, pair_s) p(tr_id, si_id)
                                WHERE p.tr_id = rid AND NOT p.si_id = ANY(converted_isf)) LOOP
                                UPDATE sensitivity_schedules
                                SET entries_json = {{Converted("entries_json", IsfFields)}}, sys_updated_at = now()
                                WHERE id = sid AND tenant_id = t.id;
                                converted_isf := converted_isf || sid;
                                n_isf := n_isf + 1;
                            END LOOP;
                        EXCEPTION WHEN data_exception THEN
                            n_left := n_left + 1;
                            RAISE NOTICE 'tenant %: target_range_schedules % left in mmol with its store: %', t.id, rid, SQLERRM;
                        END;
                    END LOOP;

                    FOR rid IN
                        SELECT p.id FROM profile_activations p
                        WHERE p.tenant_id = t.id
                          AND p.deleted_at IS NULL
                          AND lower(p.snapshot->>'Units') IN ('mmol', 'mmol/l')
                          AND (SELECT bool_and({{RawTarget}})
                               FROM jsonb_array_elements({{AsArray("p.snapshot->'Targets'")}}) e)
                    LOOP
                        SELECT p.snapshot INTO snap FROM profile_activations p WHERE p.id = rid AND p.tenant_id = t.id;
                        IF NOT {{AllRaw("snap->'Sensitivity'", RawIsf)}} THEN
                            n_left := n_left + 1;
                            RAISE NOTICE 'tenant %: profile_activations % left in mmol: its sensitivity is not all at 0.5 to 22.2', t.id, rid;
                            CONTINUE;
                        END IF;
                        BEGIN
                            snap := jsonb_set(snap, '{Targets}', {{Converted("snap->'Targets'", TargetFields)}});
                            snap := jsonb_set(snap, '{Sensitivity}', {{Converted("snap->'Sensitivity'", IsfFields)}});
                            UPDATE profile_activations SET snapshot = snap, sys_updated_at = now()
                            WHERE id = rid AND tenant_id = t.id;
                            n_snapshots := n_snapshots + 1;
                        EXCEPTION WHEN data_exception THEN
                            n_left := n_left + 1;
                            RAISE NOTICE 'tenant %: profile_activations % left in mmol: %', t.id, rid, SQLERRM;
                        END;
                    END LOOP;

                    IF n_targets + n_isf + n_snapshots + n_left > 0 THEN
                        RAISE NOTICE 'tenant %: converted % target range schedules, % sensitivity schedules, % activation snapshots from mmol; left % in mmol, to review by hand',
                            t.id, n_targets, n_isf, n_snapshots, n_left;
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
                        RAISE NOTICE 'tenant %: mmol sensitivity_schedules still holding an ISF at or under 22.2: %', t.id, left_over;
                    END IF;

                    SELECT string_agg(p.id::text, ', ') INTO left_over
                    FROM profile_activations p
                    WHERE p.tenant_id = t.id
                      AND p.deleted_at IS NULL
                      AND lower(p.snapshot->>'Units') IN ('mmol', 'mmol/l')
                      AND (EXISTS (SELECT 1 FROM jsonb_array_elements({{AsArray("p.snapshot->'Targets'")}}) e WHERE {{AnyRawTarget}})
                           OR EXISTS (SELECT 1 FROM jsonb_array_elements({{AsArray("p.snapshot->'Sensitivity'")}}) e WHERE {{AnyRawIsf}}));
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol profile_activations snapshots still holding a target or ISF at or under 22.2: %', t.id, left_over;
                    END IF;
                END LOOP;
            END $$;
            """;
    }
}
