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
    /// Each is rewritten to what it was meant to be: the alias value fills a missing canonical
    /// field, then the alias is dropped; a legacy threshold type becomes <c>threshold</c> with the
    /// direction it named; an operator-less composite becomes <c>and</c>, which is what the
    /// wizard's "combined with" preset meant. A rule the rewrite changed is marked
    /// <c>legacy_condition_converted</c>. Its confirmation readings and hysteresis are not
    /// reconstructed: AlertsRedesign dropped their columns, and the tracker reads every rule as one
    /// confirmation reading and no hysteresis, which fires sooner and closes sooner than the rule
    /// was set to, never later.
    /// </para>
    /// <para>
    /// A condition type no kind has is disabled and marked <c>condition_type_unrecognized</c>, with
    /// its parameters kept and its type set to <c>composite</c> so the row can be read. A severity
    /// other than the legacy <c>normal</c> (renamed to <c>warning</c>, as
    /// RerunAlertRuleSeverityRenameUnderTenantContext did) that no member has becomes
    /// <c>critical</c>, marked <c>severity_unrecognized</c>. A rule whose rewrite raises a data
    /// error keeps its parameters and is reported by <c>RAISE NOTICE</c>, as is every change.
    /// Rerunning changes nothing: every output is a shape the rewrite leaves alone.
    /// </para>
    /// </remarks>
    public partial class RepairPreRedesignAlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_threshold(p jsonb, hint text) RETURNS jsonb AS $fn$
                DECLARE
                    r jsonb := p;
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' THEN
                        RETURN r;
                    END IF;
                    IF jsonb_typeof(r->'threshold') = 'number' THEN
                        IF pg_temp.repair_853_absent(r->'value') THEN
                            r := jsonb_set(r, '{value}', r->'threshold');
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
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' THEN
                        RETURN r;
                    END IF;
                    IF jsonb_typeof(r->'rateThreshold') = 'number' THEN
                        IF pg_temp.repair_853_absent(r->'rate') THEN
                            r := jsonb_set(r, '{rate}', r->'rateThreshold');
                        END IF;
                        r := r - 'rateThreshold';
                    END IF;
                    RETURN r;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_signal_loss(p jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    r jsonb := p;
                BEGIN
                    IF r IS NULL OR jsonb_typeof(r) IS DISTINCT FROM 'object' THEN
                        RETURN r;
                    END IF;
                    IF pg_temp.repair_853_absent(r->'timeout_minutes') THEN
                        IF jsonb_typeof(r->'timeoutMinutes') = 'number' THEN
                            r := jsonb_set(r, '{timeout_minutes}', r->'timeoutMinutes');
                        ELSIF jsonb_typeof(r->'minutes') = 'number' THEN
                            r := jsonb_set(r, '{timeout_minutes}', r->'minutes');
                        END IF;
                    END IF;
                    IF jsonb_typeof(r->'timeout_minutes') = 'number' THEN
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

                CREATE OR REPLACE FUNCTION pg_temp.repair_853_node(node jsonb) RETURNS jsonb AS $fn$
                DECLARE
                    t text;
                    hint text;
                BEGIN
                    IF node IS NULL OR jsonb_typeof(node) IS DISTINCT FROM 'object' THEN
                        RETURN node;
                    END IF;
                    t := node->>'type';

                    IF t IN ('threshold', 'threshold_low', 'threshold_high') THEN
                        hint := CASE t WHEN 'threshold_low' THEN 'below' WHEN 'threshold_high' THEN 'above' END;
                        IF jsonb_typeof(node->'threshold') = 'object' THEN
                            RETURN jsonb_set(jsonb_set(node, '{threshold}',
                                       pg_temp.repair_853_threshold(node->'threshold', hint)),
                                   '{type}', '"threshold"');
                        END IF;
                        IF t <> 'threshold' OR jsonb_typeof(node->'threshold') = 'number'
                           OR node ? 'value' OR node ? 'direction' THEN
                            RETURN jsonb_build_object(
                                'type', 'threshold',
                                'threshold', pg_temp.repair_853_threshold(node - 'type', hint));
                        END IF;
                        RETURN node;
                    END IF;

                    IF t = 'rate_of_change' THEN
                        IF jsonb_typeof(node->'rate_of_change') = 'object' THEN
                            RETURN jsonb_set(node, '{rate_of_change}', pg_temp.repair_853_rate(node->'rate_of_change'));
                        END IF;
                        IF node ? 'rateThreshold' OR node ? 'rate' OR node ? 'direction' THEN
                            RETURN jsonb_build_object(
                                'type', 'rate_of_change',
                                'rate_of_change', pg_temp.repair_853_rate(node - 'type'));
                        END IF;
                        RETURN node;
                    END IF;

                    IF t = 'signal_loss' THEN
                        IF jsonb_typeof(node->'signal_loss') = 'object' THEN
                            RETURN jsonb_set(node, '{signal_loss}', pg_temp.repair_853_signal_loss(node->'signal_loss'));
                        END IF;
                        IF node ? 'minutes' OR node ? 'timeoutMinutes' OR node ? 'timeout_minutes' THEN
                            RETURN jsonb_build_object(
                                'type', 'signal_loss',
                                'signal_loss', pg_temp.repair_853_signal_loss(node - 'type'));
                        END IF;
                        RETURN node;
                    END IF;

                    IF t = 'composite' AND jsonb_typeof(node->'composite') = 'object' THEN
                        RETURN jsonb_set(node, '{composite}', pg_temp.repair_853_composite(node->'composite'));
                    END IF;

                    IF t IN ('not', 'sustained') AND jsonb_typeof(node->t) = 'object' THEN
                        RETURN jsonb_set(node, ARRAY[t], pg_temp.repair_853_wrapper(node->t));
                    END IF;

                    RETURN node;
                END;
                $fn$ LANGUAGE plpgsql IMMUTABLE;

                -- The rule's repaired columns, or NULL when it needs nothing.
                CREATE OR REPLACE FUNCTION pg_temp.repair_853_rule(
                    tenant uuid, rule_id uuid, ctype text, params jsonb, sev text) RETURNS jsonb AS $fn$
                DECLARE
                    lc text := lower(ctype);
                    new_type text := ctype;
                    new_params jsonb := params;
                    new_sev text := sev;
                    disable boolean := false;
                    reason text;
                    readable boolean := lc = ANY (ARRAY[
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
                    EXCEPTION WHEN data_exception OR program_limit_exceeded THEN
                        RAISE NOTICE 'alert rule % (tenant %): condition of type % left as stored: % (%)',
                            rule_id, tenant, ctype, SQLERRM, SQLSTATE;
                        new_params := params;
                    END;

                    IF reason IS NULL AND (new_type IS DISTINCT FROM ctype OR new_params IS DISTINCT FROM params) THEN
                        reason := 'legacy_condition_converted';
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

                    RAISE NOTICE 'alert rule % (tenant %): % -> %, severity % -> %, %',
                        rule_id, tenant, ctype, new_type, sev, new_sev,
                        COALESCE(reason, 'severity renamed') || CASE WHEN disable THEN ', disabled' ELSE '' END;

                    RETURN jsonb_build_object(
                        'condition_type', new_type,
                        'condition_params', new_params,
                        'severity', new_sev,
                        'disable', disable,
                        'review_reason', reason);
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
                                   severity = f.repair->>'severity',
                                   is_enabled = r.is_enabled AND NOT (f.repair->>'disable')::boolean,
                                   review_reason = COALESCE(f.repair->>'review_reason', r.review_reason)
                              FROM (SELECT id,
                                           pg_temp.repair_853_rule(t_id, id, condition_type, condition_params, severity) AS repair
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
                DROP FUNCTION IF EXISTS pg_temp.repair_853_rule(uuid, uuid, text, jsonb, text);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_node(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_wrapper(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_composite(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_signal_loss(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_rate(jsonb);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_threshold(jsonb, text);
                DROP FUNCTION IF EXISTS pg_temp.repair_853_absent(jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rewrite is one-way: the legacy shapes are what the engine cannot read.
            migrationBuilder.DropColumn(
                name: "review_reason",
                table: "alert_rules");
        }
    }
}
