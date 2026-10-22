using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Rewrites the alert rules stored before the alerts redesign into the shapes the engine reads,
    /// which AlertsRedesign's condition rewrite never did: its loop read tenant ids from
    /// <c>alert_rules</c>, and FORCE ROW LEVEL SECURITY hides every row from a migrator with no
    /// tenant context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shapes come from the writers of the time. The setup wizard stored <c>condition_type</c>
    /// <c>threshold_low</c> / <c>threshold_high</c> until the enum columns arrived, which the enum
    /// converter cannot read, so one such row stopped every rule of its tenant being read, and the
    /// cross-tenant sweep with it. It stored thresholds as <c>{threshold, direction}</c> with no
    /// <c>value</c>, read as 0: a low never fired and a high always did. Its signal loss was
    /// <c>{minutes}</c> with no <c>timeout_minutes</c>, which never fires. Its "Fast Drop" composite
    /// had no <c>operator</c>, a fault that skips the rule, and flat children
    /// (<c>{type, threshold: 100, direction}</c>, <c>{type, rateThreshold, direction}</c>). The rule
    /// editor added the alias keys <c>threshold</c>, <c>rateThreshold</c> and <c>minutes</c>
    /// beside the canonical ones.
    /// </para>
    /// <para>
    /// Where AlertsRedesign did run (a migrator that bypasses RLS), it wrapped the parameters of a
    /// type it did not know, such as <c>threshold_low</c>, into a composite child with no
    /// <c>type</c>, and the parameters of a threshold with no <c>value</c> into its threshold node
    /// and auto-resolve tree as they were. A type-less node is read by its fields: a direction with
    /// a value or threshold is a threshold, with a rate a rate of change, and minutes with no child
    /// a signal loss. Its signal loss rewrite read only <c>timeout_minutes</c>, so a wizard Sensor
    /// Lost became 15 minutes whatever it was set to; nothing kept the setting.
    /// </para>
    /// <para>
    /// Each is rewritten to what it was meant to be: the alias value, read as a number even when
    /// stored as a numeric string, fills a missing canonical field, then the alias is dropped; a
    /// legacy threshold type becomes <c>threshold</c> with the direction it named; the kinds it
    /// rewrites (threshold, rate of change, signal loss, composite, not and sustained) get their
    /// wire name when spelt in another case or without underscores; a timeout is rounded down to
    /// whole minutes, so a signal loss only ever fires sooner; an operator-less composite becomes
    /// <c>and</c>, which is what the wizard's "combined with" preset meant. A rule the rewrite
    /// changed is marked <c>legacy_condition_converted</c>, or <c>legacy_threshold_implausible</c>
    /// when a glucose threshold in it is missing, not a number, or below 40 mg/dL (likely a
    /// mmol/L value); an implausible "above" threshold fires at once, which is loud but seen. Its
    /// confirmation readings and hysteresis are not reconstructed: AlertsRedesign dropped their
    /// columns, and the tracker reads every rule as one confirmation reading and no hysteresis,
    /// which fires sooner and closes sooner than the rule was set to, never later.
    /// </para>
    /// <para>
    /// A condition type no kind has is disabled and marked <c>condition_type_unrecognized</c>, with
    /// its parameters kept and its type set to <c>composite</c> so the row can be read. A severity
    /// other than the legacy <c>normal</c> (renamed to <c>warning</c>, as
    /// RerunAlertRuleSeverityRenameUnderTenantContext did) that no member has becomes
    /// <c>critical</c>, marked <c>severity_unrecognized</c>. Every changed rule keeps what it held
    /// before in <c>pre_repair</c>, and the change is reported by <c>RAISE NOTICE</c> with both. A
    /// rule whose rewrite raises a data error keeps its trees and is reported the same way.
    /// Rerunning changes nothing: every output is a shape the rewrite leaves alone.
    /// </para>
    /// </remarks>
    public partial class RepairPreRedesignAlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pre_repair",
                table: "alert_rules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_reason",
                table: "alert_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_absent(v jsonb) RETURNS boolean AS $fn$
                    SELECT v IS NULL OR jsonb_typeof(v) = 'null';
                $fn$ LANGUAGE sql IMMUTABLE;

                -- A JSON number, or a string holding one, as a JSON number; otherwise NULL.
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_number(v jsonb) RETURNS jsonb AS $fn$
                    SELECT CASE
                        WHEN jsonb_typeof(v) = 'number' THEN v
                        WHEN jsonb_typeof(v) = 'string' AND btrim(v #>> '{}') ~ '^-?[0-9]+(\.[0-9]+)?$'
                            THEN to_jsonb(btrim(v #>> '{}')::numeric)
                    END;
                $fn$ LANGUAGE sql IMMUTABLE;

                -- The wire name of a kind this repair rewrites, whatever its case or underscores;
                -- any other kind as it is.
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_kind(t text) RETURNS text AS $fn$
                    SELECT CASE replace(lower(t), '_', '')
                        WHEN 'threshold' THEN 'threshold'
                        WHEN 'thresholdlow' THEN 'threshold_low'
                        WHEN 'thresholdhigh' THEN 'threshold_high'
                        WHEN 'rateofchange' THEN 'rate_of_change'
                        WHEN 'signalloss' THEN 'signal_loss'
                        WHEN 'composite' THEN 'composite'
                        WHEN 'not' THEN 'not'
                        WHEN 'sustained' THEN 'sustained'
                        ELSE t
                    END;
                $fn$ LANGUAGE sql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_threshold(p jsonb, hint text) RETURNS jsonb AS $fn$
                DECLARE
                    r jsonb := p;
                    alias jsonb;
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' THEN
                        RETURN r;
                    END IF;
                    IF jsonb_typeof(r->'value') = 'string' AND pg_temp.repair_853_number(r->'value') IS NOT NULL THEN
                        r := jsonb_set(r, '{value}', pg_temp.repair_853_number(r->'value'));
                    END IF;
                    alias := pg_temp.repair_853_number(r->'threshold');
                    IF alias IS NOT NULL THEN
                        IF pg_temp.repair_853_absent(r->'value') THEN
                            r := jsonb_set(r, '{value}', alias);
                        END IF;
                        r := r - 'threshold';
                    END IF;
                    IF hint IS NOT NULL AND pg_temp.repair_853_absent(r->'direction') THEN
                        r := jsonb_set(r, '{direction}', to_jsonb(hint));
                    END IF;
                    RETURN r;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_rate(p jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    r jsonb := p;
                    alias jsonb;
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' THEN
                        RETURN r;
                    END IF;
                    IF jsonb_typeof(r->'rate') = 'string' AND pg_temp.repair_853_number(r->'rate') IS NOT NULL THEN
                        r := jsonb_set(r, '{rate}', pg_temp.repair_853_number(r->'rate'));
                    END IF;
                    alias := pg_temp.repair_853_number(r->'rateThreshold');
                    IF alias IS NOT NULL THEN
                        IF pg_temp.repair_853_absent(r->'rate') THEN
                            r := jsonb_set(r, '{rate}', alias);
                        END IF;
                        r := r - 'rateThreshold';
                    END IF;
                    RETURN r;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_signal_loss(p jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    r jsonb := p;
                    timeout jsonb;
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' THEN
                        RETURN r;
                    END IF;
                    timeout := COALESCE(
                        pg_temp.repair_853_number(r->'timeout_minutes'),
                        pg_temp.repair_853_number(r->'timeoutMinutes'),
                        pg_temp.repair_853_number(r->'minutes'));
                    IF timeout IS NOT NULL THEN
                        r := jsonb_set(r, '{timeout_minutes}', to_jsonb(floor((timeout #>> '{}')::numeric)::int));
                        r := r - 'timeoutMinutes' - 'minutes';
                    END IF;
                    RETURN r;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_composite(c jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    r jsonb := c;
                    children jsonb;
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' OR jsonb_typeof(r->'conditions') IS DISTINCT FROM 'array' THEN
                        RETURN r;
                    END IF;
                    SELECT COALESCE(jsonb_agg(pg_temp.repair_853_node(e) ORDER BY i), '[]'::jsonb)
                      INTO children
                      FROM jsonb_array_elements(r->'conditions') WITH ORDINALITY AS a(e, i);
                    r := jsonb_set(r, '{conditions}', children);
                    IF jsonb_array_length(children) > 0 AND pg_temp.repair_853_absent(r->'operator') THEN
                        r := jsonb_set(r, '{operator}', '"and"');
                    END IF;
                    RETURN r;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_wrapper(w jsonb) RETURNS jsonb AS $fn$
                BEGIN
                    IF w IS NULL OR jsonb_typeof(w) IS DISTINCT FROM 'object' OR jsonb_typeof(w->'child') IS DISTINCT FROM 'object' THEN
                        RETURN w;
                    END IF;
                    RETURN jsonb_set(w, '{child}', pg_temp.repair_853_node(w->'child'));
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                -- A node's payload under its wire name or the camelCase one, moved to the wire name
                -- and passed through the kind's repair.
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_payload(node jsonb, kind text, camel text, repaired jsonb) RETURNS jsonb AS $fn$
                    SELECT jsonb_set((node - camel) || jsonb_build_object('type', kind), ARRAY[kind], repaired);
                $fn$ LANGUAGE sql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_node(node jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    t text;
                    hint text;
                    payload jsonb;
                BEGIN
                    IF node IS NULL OR jsonb_typeof(node) IS DISTINCT FROM 'object' THEN
                        RETURN node;
                    END IF;

                    IF pg_temp.repair_853_absent(node->'type') THEN
                        IF node ? 'direction' AND (node ? 'value' OR node ? 'threshold')
                           AND lower(node->>'direction') IN ('above', 'below') THEN
                            RETURN jsonb_build_object('type', 'threshold',
                                'threshold', pg_temp.repair_853_threshold(node, NULL));
                        END IF;
                        IF node ? 'direction' AND (node ? 'rate' OR node ? 'rateThreshold') THEN
                            RETURN jsonb_build_object('type', 'rate_of_change',
                                'rate_of_change', pg_temp.repair_853_rate(node));
                        END IF;
                        IF NOT node ? 'child'
                           AND (node ? 'minutes' OR node ? 'timeout_minutes' OR node ? 'timeoutMinutes') THEN
                            RETURN jsonb_build_object('type', 'signal_loss',
                                'signal_loss', pg_temp.repair_853_signal_loss(node));
                        END IF;
                        IF jsonb_typeof(node->'conditions') = 'array' THEN
                            RETURN jsonb_build_object('type', 'composite',
                                'composite', pg_temp.repair_853_composite(node));
                        END IF;
                        RETURN node;
                    END IF;

                    t := pg_temp.repair_853_kind(node->>'type');

                    IF t IN ('threshold', 'threshold_low', 'threshold_high') THEN
                        hint := CASE t WHEN 'threshold_low' THEN 'below' WHEN 'threshold_high' THEN 'above' END;
                        IF jsonb_typeof(node->'threshold') = 'object' THEN
                            RETURN pg_temp.repair_853_payload(node, 'threshold', 'threshold',
                                pg_temp.repair_853_threshold(node->'threshold', hint));
                        END IF;
                        IF t <> 'threshold' OR pg_temp.repair_853_number(node->'threshold') IS NOT NULL
                           OR node ? 'value' OR node ? 'direction' THEN
                            RETURN jsonb_build_object(
                                'type', 'threshold',
                                'threshold', pg_temp.repair_853_threshold(node - 'type', hint));
                        END IF;
                        RETURN jsonb_set(node, '{type}', '"threshold"');
                    END IF;

                    IF t = 'rate_of_change' THEN
                        payload := COALESCE(node->'rate_of_change', node->'rateOfChange');
                        IF jsonb_typeof(payload) = 'object' THEN
                            RETURN pg_temp.repair_853_payload(node, t, 'rateOfChange', pg_temp.repair_853_rate(payload));
                        END IF;
                        IF node ? 'rateThreshold' OR node ? 'rate' OR node ? 'direction' THEN
                            RETURN jsonb_build_object('type', t, t, pg_temp.repair_853_rate(node - 'type'));
                        END IF;
                        RETURN jsonb_set(node, '{type}', to_jsonb(t));
                    END IF;

                    IF t = 'signal_loss' THEN
                        payload := COALESCE(node->'signal_loss', node->'signalLoss');
                        IF jsonb_typeof(payload) = 'object' THEN
                            RETURN pg_temp.repair_853_payload(node, t, 'signalLoss', pg_temp.repair_853_signal_loss(payload));
                        END IF;
                        IF node ? 'minutes' OR node ? 'timeoutMinutes' OR node ? 'timeout_minutes' THEN
                            RETURN jsonb_build_object('type', t, t, pg_temp.repair_853_signal_loss(node - 'type'));
                        END IF;
                        RETURN jsonb_set(node, '{type}', to_jsonb(t));
                    END IF;

                    IF t = 'composite' AND jsonb_typeof(node->'composite') = 'object' THEN
                        RETURN jsonb_set(jsonb_set(node, '{type}', to_jsonb(t)),
                            '{composite}', pg_temp.repair_853_composite(node->'composite'));
                    END IF;

                    IF t IN ('not', 'sustained') AND jsonb_typeof(node->t) = 'object' THEN
                        RETURN jsonb_set(jsonb_set(node, '{type}', to_jsonb(t)),
                            ARRAY[t], pg_temp.repair_853_wrapper(node->t));
                    END IF;

                    RETURN node;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                -- Whether a glucose threshold in the rule's tree is missing, not a number, or below
                -- 40 mg/dL.
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_implausible(ctype text, params jsonb) RETURNS boolean AS $fn$
                    SELECT CASE
                            WHEN ctype <> 'threshold' THEN false
                            WHEN jsonb_typeof(params->'value') IS DISTINCT FROM 'number' THEN true
                            ELSE (params->>'value')::numeric < 40
                        END
                        OR (jsonb_typeof(params) IN ('object', 'array')
                            AND jsonb_path_exists(params,
                                'lax $.** ? (@.type == "threshold" && (!exists(@.threshold.value) || @.threshold.value.type() != "number" || @.threshold.value < 40))'));
                $fn$ LANGUAGE sql IMMUTABLE;

                -- The rule's repaired columns, or NULL when it needs nothing.
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_rule(
                    tenant uuid, rule_id uuid, ctype text, params jsonb, sev text,
                    enabled boolean, ar_enabled boolean, ar_params jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    lc text := pg_temp.repair_853_kind(ctype);
                    new_type text := ctype;
                    new_params jsonb := params;
                    new_ar jsonb := ar_params;
                    new_sev text := sev;
                    disable boolean := false;
                    reason text;
                    readable boolean := lower(ctype) = ANY (ARRAY[
                        'threshold', 'rate_of_change', 'signal_loss', 'composite', 'not', 'sustained',
                        'staleness', 'predicted', 'trend', 'time_of_day', 'iob', 'cob', 'reservoir',
                        'site_age', 'sensor_age', 'alert_state', 'loop_stale', 'loop_enaction_stale',
                        'pump_suspended', 'pump_battery', 'temp_basal', 'uploader_battery',
                        'override_active', 'sensitivity_ratio', 'do_not_disturb', 'glucose_bucket',
                        'time_since_last_carb', 'time_since_last_bolus', 'day_of_week', 'pump_state',
                        'state_span_active', 'sleep_session_active', 'tracker_age',
                        'rateofchange', 'signalloss', 'timeofday', 'siteage', 'sensorage', 'alertstate',
                        'loopstale', 'loopenactionstale', 'pumpsuspended', 'pumpbattery', 'tempbasal',
                        'uploaderbattery', 'overrideactive', 'sensitivityratio', 'donotdisturb',
                        'glucosebucket', 'timesincelastcarb', 'timesincelastbolus', 'dayofweek',
                        'pumpstate', 'statespanactive', 'sleepsessionactive', 'trackerage'])
                        OR ctype ~ '^[0-9]+$';
                BEGIN
                    IF lc IN ('threshold_low', 'threshold_high') THEN
                        new_type := 'threshold';
                    ELSIF lc IN ('threshold', 'rate_of_change', 'signal_loss', 'composite', 'not', 'sustained') THEN
                        new_type := lc;
                    ELSIF NOT readable THEN
                        new_type := 'composite';
                        disable := true;
                        reason := 'condition_type_unrecognized';
                    END IF;

                    BEGIN
                        new_params := CASE
                            WHEN lc IN ('threshold', 'threshold_low', 'threshold_high') THEN
                                pg_temp.repair_853_threshold(params,
                                    CASE lc WHEN 'threshold_low' THEN 'below' WHEN 'threshold_high' THEN 'above' END)
                            WHEN lc = 'rate_of_change' THEN pg_temp.repair_853_rate(params)
                            WHEN lc = 'signal_loss' THEN pg_temp.repair_853_signal_loss(params)
                            WHEN lc = 'composite' THEN pg_temp.repair_853_composite(params)
                            WHEN lc IN ('not', 'sustained') THEN pg_temp.repair_853_wrapper(params)
                            ELSE params
                        END;
                        new_ar := pg_temp.repair_853_node(ar_params);
                    EXCEPTION WHEN data_exception OR program_limit_exceeded THEN
                        RAISE NOTICE 'alert rule % (tenant %): % % left as stored: % (%)',
                            rule_id, tenant, ctype, params, SQLERRM, SQLSTATE;
                        new_params := params;
                        new_ar := ar_params;
                    END;

                    IF reason IS NULL AND (new_type IS DISTINCT FROM ctype
                                           OR new_params IS DISTINCT FROM params
                                           OR new_ar IS DISTINCT FROM ar_params) THEN
                        reason := CASE
                            WHEN pg_temp.repair_853_implausible(new_type, new_params) THEN 'legacy_threshold_implausible'
                            ELSE 'legacy_condition_converted'
                        END;
                    END IF;

                    IF lower(sev) = 'normal' THEN
                        new_sev := 'warning';
                    ELSIF lower(sev) NOT IN ('critical', 'warning', 'info') THEN
                        new_sev := 'critical';
                        reason := COALESCE(reason, 'severity_unrecognized');
                    END IF;

                    IF reason IS NULL AND new_sev IS NOT DISTINCT FROM sev THEN
                        RETURN NULL;
                    END IF;

                    RAISE NOTICE 'alert rule % (tenant %): % % -> % %; auto-resolve % -> %; severity % -> %; %',
                        rule_id, tenant, ctype, params, new_type, new_params, ar_params, new_ar, sev, new_sev,
                        COALESCE(reason, 'severity renamed') || CASE WHEN disable THEN ', disabled' ELSE '' END;

                    RETURN jsonb_build_object(
                        'condition_type', new_type,
                        'condition_params', new_params,
                        'auto_resolve_params', new_ar,
                        'severity', new_sev,
                        'disable', disable,
                        'review_reason', reason,
                        'pre_repair', jsonb_build_object(
                            'condition_type', ctype,
                            'condition_params', params,
                            'auto_resolve_enabled', ar_enabled,
                            'auto_resolve_params', ar_params,
                            'severity', sev,
                            'is_enabled', enabled));
                END;
                $fn$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        BEGIN
                            UPDATE alert_rules AS r
                               SET condition_type = f.repair->>'condition_type',
                                   condition_params = f.repair->'condition_params',
                                   auto_resolve_params = CASE
                                       WHEN jsonb_typeof(f.repair->'auto_resolve_params') = 'null' THEN NULL
                                       ELSE f.repair->'auto_resolve_params'
                                   END,
                                   severity = f.repair->>'severity',
                                   is_enabled = r.is_enabled AND NOT (f.repair->>'disable')::boolean,
                                   review_reason = COALESCE(f.repair->>'review_reason', r.review_reason),
                                   pre_repair = COALESCE(r.pre_repair, f.repair->'pre_repair')
                              FROM (SELECT id,
                                           pg_temp.repair_853_rule(t_id, id, condition_type, condition_params, severity,
                                               is_enabled, auto_resolve_enabled, auto_resolve_params) AS repair
                                      FROM alert_rules
                                     WHERE tenant_id = t_id) AS f
                             WHERE r.id = f.id
                               AND r.tenant_id = t_id
                               AND f.repair IS NOT NULL;
                        EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN
                            RAISE NOTICE 'alert rules of tenant % left as stored: % (%)', t_id, SQLERRM, SQLSTATE;
                        END;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS pg_temp.repair_853_rule(uuid, uuid, text, jsonb, text, boolean, boolean, jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_implausible(text, jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_node(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_payload(jsonb, text, text, jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_wrapper(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_composite(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_signal_loss(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_rate(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_threshold(jsonb, text);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_kind(text);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_number(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_absent(jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rewrite is one-way: the legacy shapes are what the engine cannot read.
            migrationBuilder.DropColumn(
                name: "pre_repair",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "review_reason",
                table: "alert_rules");
        }
    }
}
