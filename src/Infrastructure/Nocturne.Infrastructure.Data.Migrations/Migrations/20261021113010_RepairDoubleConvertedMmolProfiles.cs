using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Undoes the extra mmol-to-mg/dL conversion a mmol profile's targets and ISF took each time a
    /// client read it back before <c>ProfileProjectionService</c> converted it to mmol (#2278) and
    /// uploaded it again: the read served the stored mg/dL labelled mmol, and the write multiplied it
    /// by 18.0182 and rounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Targets. A live target schedule is double-converted when a live owner's units are mmol and every
    /// low and high target exceeds 400 mg/dL (22.2 mmol/L): one conversion of any target a client sends
    /// stays at or under that, and a second of any target of 23 mg/dL or more lands above it. Each
    /// conversion stored <c>round(m * 18.0182)</c> of a whole mg/dL <c>m</c>, so <c>round(v / 18.0182)</c>
    /// recovers <c>m</c> exactly. A step is taken while every target exceeds 400 and only when every value
    /// is such an image; the result must be back at or under 400 everywhere, else the row is left.
    /// </para>
    /// <para>
    /// ISF. An ISF is divided only with a witness that it took the same uploads as the targets: it is
    /// the live ISF schedule of the same store (same legacy id, or same correlation and profile name),
    /// with the same <c>timestamp</c> and a <c>sys_updated_at</c> within a minute of the targets', which
    /// <c>ProfileDecomposer</c> writes in one batch and a later V4 edit of either moves apart. It must
    /// also take exactly as many steps as its targets under the same rule, so a step is taken only while
    /// every ISF value exceeds 400 mg/dL/U: a correctly stored mmol ISF (at most 22.2 mmol/L/U) is never
    /// divided, and an ISF converted more or fewer times than its targets is left. An ISF that fails
    /// either test is left as it is, which errs towards an ISF too large rather than too small.
    /// </para>
    /// <para>
    /// A profile-activation snapshot, built from the switch's <c>profileJson</c> with the same
    /// conversion, is one write, so its own targets are its ISF's witness; it takes the same rules. The
    /// <c>profileJson</c> is served as uploaded and is left alone; <c>ProfileActivationSnapshots</c>
    /// undoes the same conversion when it rebuilds a snapshot.
    /// </para>
    /// <para>
    /// A repaired row is no longer selected, so running this again changes nothing. Every row it
    /// selects but leaves, and every live mmol target or ISF still above 400 afterwards, is reported by a
    /// notice. Repaired rows take a new <c>sys_updated_at</c>, so a v3 history client fetches the
    /// corrected profile. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC.
    /// </para>
    /// </remarks>
    public partial class RepairDoubleConvertedMmolProfiles : Migration
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

        private const string RepairSql = """
            DO $$
            DECLARE
                f CONSTANT numeric := 18.0182;
                t RECORD;
                ids uuid[];
                rid uuid;
                sid uuid;
                repaired_isf uuid[];
                r_legacy text;
                r_correlation uuid;
                r_profile text;
                r_timestamp timestamp;
                r_updated timestamp;
                s_timestamp timestamp;
                s_updated timestamp;
                entries jsonb;
                isf jsonb;
                snap jsonb;
                steps int;
                taken int;
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

                    SELECT array_agg(tr.id) INTO ids
                    FROM target_range_schedules tr
                    WHERE tr.tenant_id = t.id
                      AND tr.deleted_at IS NULL
                      AND jsonb_typeof(tr.entries_json) = 'array'
                      AND jsonb_array_length(tr.entries_json) > 0
                      AND EXISTS (
                          SELECT 1 FROM therapy_settings ts
                          WHERE ts.tenant_id = t.id
                            AND ts.deleted_at IS NULL
                            AND lower(ts.units) IN ('mmol', 'mmol/l')
                            AND (ts.legacy_id = tr.legacy_id
                                 OR (ts.correlation_id = tr.correlation_id AND ts.profile_name = tr.profile_name)))
                      AND (SELECT min(least((e->>'Low')::numeric, (e->>'High')::numeric))
                           FROM jsonb_array_elements(tr.entries_json) e) > 400;

                    FOREACH rid IN ARRAY COALESCE(ids, ARRAY[]::uuid[]) LOOP
                        SELECT tr.legacy_id, tr.correlation_id, tr.profile_name, tr.timestamp, tr.sys_updated_at, tr.entries_json
                        INTO r_legacy, r_correlation, r_profile, r_timestamp, r_updated, entries
                        FROM target_range_schedules tr WHERE tr.id = rid;

                        steps := 0;
                        WHILE (SELECT min(least((e->>'Low')::numeric, (e->>'High')::numeric))
                               FROM jsonb_array_elements(entries) e) > 400 AND steps < 4 LOOP
                            IF EXISTS (
                                SELECT 1 FROM jsonb_array_elements(entries) e
                                CROSS JOIN LATERAL (SELECT (e->>'Low')::numeric AS x
                                                    UNION ALL SELECT (e->>'High')::numeric) v
                                WHERE v.x IS NULL OR abs(round(v.x / f) * f - v.x) > 0.5) THEN
                                steps := -1;
                                EXIT;
                            END IF;
                            entries := (SELECT jsonb_agg(e || jsonb_build_object(
                                                'Low', round((e->>'Low')::numeric / f),
                                                'High', round((e->>'High')::numeric / f)) ORDER BY o)
                                        FROM jsonb_array_elements(entries) WITH ORDINALITY x(e, o));
                            steps := steps + 1;
                        END LOOP;

                        IF steps <= 0 OR (SELECT max(greatest((e->>'Low')::numeric, (e->>'High')::numeric))
                                          FROM jsonb_array_elements(entries) e) > 400 THEN
                            n_skipped := n_skipped + 1;
                            RAISE NOTICE 'tenant %: target_range_schedules % left as it is: not a repeated conversion it can undo', t.id, rid;
                            CONTINUE;
                        END IF;

                        UPDATE target_range_schedules SET entries_json = entries, sys_updated_at = now()
                        WHERE id = rid AND tenant_id = t.id;
                        n_targets := n_targets + 1;

                        SELECT array_agg(si.id) INTO ids
                        FROM sensitivity_schedules si
                        WHERE si.tenant_id = t.id
                          AND si.deleted_at IS NULL
                          AND (si.legacy_id = r_legacy
                               OR (si.correlation_id = r_correlation AND si.profile_name = r_profile))
                          AND NOT si.id = ANY(repaired_isf);

                        FOREACH sid IN ARRAY COALESCE(ids, ARRAY[]::uuid[]) LOOP
                            SELECT si.entries_json, si.timestamp, si.sys_updated_at
                            INTO isf, s_timestamp, s_updated
                            FROM sensitivity_schedules si WHERE si.id = sid;

                            IF s_timestamp IS DISTINCT FROM r_timestamp
                               OR abs(extract(epoch FROM s_updated - r_updated)) > 60 THEN
                                n_skipped := n_skipped + 1;
                                RAISE NOTICE 'tenant %: sensitivity_schedules % left as it is: not written with target_range_schedules %', t.id, sid, rid;
                                CONTINUE;
                            END IF;

                            taken := 0;
                            IF jsonb_typeof(isf) = 'array' AND jsonb_array_length(isf) > 0 THEN
                                WHILE (SELECT min((e->>'Value')::numeric) FROM jsonb_array_elements(isf) e) > 400
                                      AND taken < 4 LOOP
                                    IF EXISTS (
                                        SELECT 1 FROM jsonb_array_elements(isf) e
                                        WHERE (e->>'Value')::numeric IS NULL
                                           OR abs(round((e->>'Value')::numeric / f) * f - (e->>'Value')::numeric) > 0.5) THEN
                                        taken := -1;
                                        EXIT;
                                    END IF;
                                    isf := (SELECT jsonb_agg(e || jsonb_build_object(
                                                    'Value', round((e->>'Value')::numeric / f)) ORDER BY o)
                                            FROM jsonb_array_elements(isf) WITH ORDINALITY x(e, o));
                                    taken := taken + 1;
                                END LOOP;
                            END IF;

                            IF taken <> steps
                               OR (SELECT max((e->>'Value')::numeric) FROM jsonb_array_elements(isf) e) > 400 THEN
                                n_skipped := n_skipped + 1;
                                RAISE NOTICE 'tenant %: sensitivity_schedules % left as it is: not the % conversions of its targets', t.id, sid, steps;
                                CONTINUE;
                            END IF;

                            UPDATE sensitivity_schedules SET entries_json = isf, sys_updated_at = now()
                            WHERE id = sid AND tenant_id = t.id;
                            repaired_isf := repaired_isf || sid;
                            n_isf := n_isf + 1;
                        END LOOP;
                    END LOOP;

                    SELECT array_agg(p.id) INTO ids
                    FROM profile_activations p
                    WHERE p.tenant_id = t.id
                      AND p.deleted_at IS NULL
                      AND lower(p.snapshot->>'Units') IN ('mmol', 'mmol/l')
                      AND jsonb_typeof(p.snapshot->'Targets') = 'array'
                      AND jsonb_array_length(p.snapshot->'Targets') > 0
                      AND (SELECT min(least((e->>'Low')::numeric, (e->>'High')::numeric))
                           FROM jsonb_array_elements(p.snapshot->'Targets') e) > 400;

                    FOREACH rid IN ARRAY COALESCE(ids, ARRAY[]::uuid[]) LOOP
                        SELECT p.snapshot INTO snap FROM profile_activations p WHERE p.id = rid;
                        entries := snap->'Targets';

                        steps := 0;
                        WHILE (SELECT min(least((e->>'Low')::numeric, (e->>'High')::numeric))
                               FROM jsonb_array_elements(entries) e) > 400 AND steps < 4 LOOP
                            IF EXISTS (
                                SELECT 1 FROM jsonb_array_elements(entries) e
                                CROSS JOIN LATERAL (SELECT (e->>'Low')::numeric AS x
                                                    UNION ALL SELECT (e->>'High')::numeric) v
                                WHERE v.x IS NULL OR abs(round(v.x / f) * f - v.x) > 0.5) THEN
                                steps := -1;
                                EXIT;
                            END IF;
                            entries := (SELECT jsonb_agg(e || jsonb_build_object(
                                                'Low', round((e->>'Low')::numeric / f),
                                                'High', round((e->>'High')::numeric / f)) ORDER BY o)
                                        FROM jsonb_array_elements(entries) WITH ORDINALITY x(e, o));
                            steps := steps + 1;
                        END LOOP;

                        IF steps <= 0 OR (SELECT max(greatest((e->>'Low')::numeric, (e->>'High')::numeric))
                                          FROM jsonb_array_elements(entries) e) > 400 THEN
                            n_skipped := n_skipped + 1;
                            RAISE NOTICE 'tenant %: profile_activations % left as it is: not a repeated conversion it can undo', t.id, rid;
                            CONTINUE;
                        END IF;
                        snap := jsonb_set(snap, '{Targets}', entries);

                        isf := snap->'Sensitivity';
                        taken := 0;
                        IF jsonb_typeof(isf) = 'array' AND jsonb_array_length(isf) > 0 THEN
                            WHILE (SELECT min((e->>'Value')::numeric) FROM jsonb_array_elements(isf) e) > 400
                                  AND taken < 4 LOOP
                                IF EXISTS (
                                    SELECT 1 FROM jsonb_array_elements(isf) e
                                    WHERE (e->>'Value')::numeric IS NULL
                                       OR abs(round((e->>'Value')::numeric / f) * f - (e->>'Value')::numeric) > 0.5) THEN
                                    taken := -1;
                                    EXIT;
                                END IF;
                                isf := (SELECT jsonb_agg(e || jsonb_build_object(
                                                'Value', round((e->>'Value')::numeric / f)) ORDER BY o)
                                        FROM jsonb_array_elements(isf) WITH ORDINALITY x(e, o));
                                taken := taken + 1;
                            END LOOP;
                        END IF;

                        IF taken = steps AND (SELECT max((e->>'Value')::numeric) FROM jsonb_array_elements(isf) e) <= 400 THEN
                            snap := jsonb_set(snap, '{Sensitivity}', isf);
                        ELSIF jsonb_typeof(snap->'Sensitivity') = 'array' AND jsonb_array_length(snap->'Sensitivity') > 0 THEN
                            n_skipped := n_skipped + 1;
                            RAISE NOTICE 'tenant %: profile_activations % sensitivity left as it is: not the % conversions of its targets', t.id, rid, steps;
                        END IF;

                        UPDATE profile_activations SET snapshot = snap, sys_updated_at = now()
                        WHERE id = rid AND tenant_id = t.id;
                        n_snapshots := n_snapshots + 1;
                    END LOOP;

                    IF n_targets + n_isf + n_snapshots + n_skipped > 0 THEN
                        RAISE NOTICE 'tenant %: repaired % target range schedules, % sensitivity schedules, % activation snapshots; left % as they were',
                            t.id, n_targets, n_isf, n_snapshots, n_skipped;
                    END IF;

                    SELECT string_agg(tr.id::text, ', ') INTO left_over
                    FROM target_range_schedules tr
                    WHERE tr.tenant_id = t.id
                      AND tr.deleted_at IS NULL
                      AND EXISTS (
                          SELECT 1 FROM therapy_settings ts
                          WHERE ts.tenant_id = t.id
                            AND ts.deleted_at IS NULL
                            AND lower(ts.units) IN ('mmol', 'mmol/l')
                            AND (ts.legacy_id = tr.legacy_id
                                 OR (ts.correlation_id = tr.correlation_id AND ts.profile_name = tr.profile_name)))
                      AND EXISTS (SELECT 1 FROM jsonb_array_elements(tr.entries_json) e
                                  WHERE greatest((e->>'Low')::numeric, (e->>'High')::numeric) > 400);
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol target_range_schedules still above 400 mg/dL: %', t.id, left_over;
                    END IF;

                    SELECT string_agg(si.id::text, ', ') INTO left_over
                    FROM sensitivity_schedules si
                    WHERE si.tenant_id = t.id
                      AND si.deleted_at IS NULL
                      AND EXISTS (
                          SELECT 1 FROM therapy_settings ts
                          WHERE ts.tenant_id = t.id
                            AND ts.deleted_at IS NULL
                            AND lower(ts.units) IN ('mmol', 'mmol/l')
                            AND (ts.legacy_id = si.legacy_id
                                 OR (ts.correlation_id = si.correlation_id AND ts.profile_name = si.profile_name)))
                      AND EXISTS (SELECT 1 FROM jsonb_array_elements(si.entries_json) e
                                  WHERE (e->>'Value')::numeric > 400);
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol sensitivity_schedules still above 400 mg/dL/U: %', t.id, left_over;
                    END IF;

                    SELECT string_agg(p.id::text, ', ') INTO left_over
                    FROM profile_activations p
                    WHERE p.tenant_id = t.id
                      AND p.deleted_at IS NULL
                      AND lower(p.snapshot->>'Units') IN ('mmol', 'mmol/l')
                      AND (EXISTS (SELECT 1 FROM jsonb_array_elements(
                                       CASE WHEN jsonb_typeof(p.snapshot->'Targets') = 'array' THEN p.snapshot->'Targets' ELSE '[]'::jsonb END) e
                                   WHERE greatest((e->>'Low')::numeric, (e->>'High')::numeric) > 400)
                           OR EXISTS (SELECT 1 FROM jsonb_array_elements(
                                       CASE WHEN jsonb_typeof(p.snapshot->'Sensitivity') = 'array' THEN p.snapshot->'Sensitivity' ELSE '[]'::jsonb END) e
                                   WHERE (e->>'Value')::numeric > 400));
                    IF left_over IS NOT NULL THEN
                        RAISE NOTICE 'tenant %: mmol profile_activations snapshots still above 400 mg/dL: %', t.id, left_over;
                    END IF;
                END LOOP;
            END $$;
            """;
    }
}
