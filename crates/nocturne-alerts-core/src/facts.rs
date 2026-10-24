//! The context facts each condition kind reads, and the kinds of a stored rule
//! (`docs/alerts/engine-semantics.md` §5.2).

use serde_json::Value;

use crate::engine::{WireRule, parse_auto_resolve};
use crate::enums::{WireEnum, wire_enum};
use crate::leaf_identity::collect_leaves;
use crate::model::{ConditionKind, Node, parse_payload};

wire_enum! {
    /// A group of [`SensorContext`](crate::context::SensorContext) fields
    /// that a host fills from one source; engine-semantics.md §5.2 lists each
    /// group's fields. [`WireEnum::name`] is the wire name. `now` is not a
    /// fact: it is an argument to evaluation, not context.
    pub enum ContextFact {
        Glucose = "glucose",
        Predictions = "predictions",
        Insulin = "insulin",
        Carbs = "carbs",
        Pump = "pump",
        Aps = "aps",
        Uploader = "uploader",
        StateSpans = "state_spans",
        DeviceAges = "device_ages",
        Trackers = "trackers",
        Sleep = "sleep",
        ActiveAlerts = "active_alerts",
        DoNotDisturb = "do_not_disturb",
        TimeZone = "time_zone",
    }
}

/// The facts `kind`'s own evaluator reads. A container reads none; the facts
/// beneath it are its children's ([`rule_reads`]).
#[must_use]
pub fn reads(kind: ConditionKind) -> &'static [ContextFact] {
    use ContextFact as F;
    match kind {
        ConditionKind::Threshold
        | ConditionKind::RateOfChange
        | ConditionKind::SignalLoss
        | ConditionKind::Staleness
        | ConditionKind::Trend
        | ConditionKind::GlucoseBucket => &[F::Glucose],
        ConditionKind::Predicted => &[F::Predictions],
        ConditionKind::Iob | ConditionKind::TempBasal | ConditionKind::TimeSinceLastBolus => {
            &[F::Insulin]
        }
        ConditionKind::Cob | ConditionKind::TimeSinceLastCarb => &[F::Carbs],
        ConditionKind::Reservoir | ConditionKind::PumpBattery => &[F::Pump],
        ConditionKind::PumpSuspended => &[F::Pump, F::StateSpans],
        ConditionKind::LoopStale
        | ConditionKind::LoopEnactionStale
        | ConditionKind::SensitivityRatio => &[F::Aps],
        ConditionKind::UploaderBattery => &[F::Uploader],
        ConditionKind::OverrideActive
        | ConditionKind::PumpState
        | ConditionKind::StateSpanActive => &[F::StateSpans],
        ConditionKind::SiteAge | ConditionKind::SensorAge => &[F::DeviceAges],
        ConditionKind::TrackerAge => &[F::Trackers],
        ConditionKind::SleepSessionActive => &[F::Sleep],
        ConditionKind::AlertState => &[F::ActiveAlerts],
        ConditionKind::DoNotDisturb => &[F::DoNotDisturb],
        ConditionKind::TimeOfDay | ConditionKind::DayOfWeek => &[F::TimeZone],
        ConditionKind::Composite | ConditionKind::Not | ConditionKind::Sustained => &[],
    }
}

/// A stored rule's condition trees, as [`WireRule`] carries them.
///
/// The root kind is `condition_type`, exactly a kind's wire name; anything
/// else gives the rule no kinds. The root kind counts whatever the
/// body. The body's kinds count only when it can be evaluated, nested kinds
/// resolving as node dispatch resolves them (engine-semantics.md §1.2). The
/// auto-resolve tree's kinds count whenever `auto_resolve_enabled` and the
/// tree is present, not `null` and evaluable, even when the body is not and
/// evaluation would skip the rule.
#[derive(Debug, Clone, Copy)]
pub struct RuleConditions<'a> {
    pub condition_type: &'a str,
    pub condition_params: &'a Value,
    pub auto_resolve_enabled: bool,
    pub auto_resolve_params: Option<&'a Value>,
}

impl<'a> RuleConditions<'a> {
    /// A rule with auto-resolve disabled.
    #[must_use]
    pub fn body(condition_type: &'a str, condition_params: &'a Value) -> Self {
        Self {
            condition_type,
            condition_params,
            auto_resolve_enabled: false,
            auto_resolve_params: None,
        }
    }

    pub(crate) fn kinds(&self) -> Vec<ConditionKind> {
        let Some(root) = ConditionKind::from_wire(self.condition_type) else {
            return Vec::new();
        };
        let body = parse_payload(root, self.condition_params)
            .ok()
            .map(Node::from);
        let auto_resolve = self
            .auto_resolve_enabled
            .then(|| parse_auto_resolve(self.auto_resolve_params))
            .flatten();
        let leaves = [body, auto_resolve]
            .into_iter()
            .flatten()
            .flat_map(|tree| leaf_kinds(&tree));
        std::iter::once(root).chain(leaves).collect()
    }
}

impl<'a> From<&'a WireRule> for RuleConditions<'a> {
    fn from(rule: &'a WireRule) -> Self {
        Self {
            condition_type: &rule.condition_type,
            condition_params: &rule.condition_params,
            auto_resolve_enabled: rule.auto_resolve_enabled,
            auto_resolve_params: rule.auto_resolve_params.as_ref(),
        }
    }
}

fn leaf_kinds(tree: &Node) -> Vec<ConditionKind> {
    collect_leaves(tree).into_iter().map(Node::kind).collect()
}

/// Every fact any kind of `rule` ([`RuleConditions`]) reads, once each, in
/// [`ContextFact::ALL`] order.
#[must_use]
pub fn rule_reads(rule: &RuleConditions<'_>) -> Vec<ContextFact> {
    let kinds = rule.kinds();
    ContextFact::ALL
        .iter()
        .copied()
        .filter(|fact| kinds.iter().any(|&kind| reads(kind).contains(fact)))
        .collect()
}

/// Whether any kind of `rule` ([`RuleConditions`]) reads one of `facts`. A
/// root kind that reads one is true whatever its body.
#[must_use]
pub fn references_any(rule: &RuleConditions<'_>, facts: &[ContextFact]) -> bool {
    rule_reads(rule).iter().any(|fact| facts.contains(fact))
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use chrono::{DateTime, Duration, Utc};
    use rust_decimal::Decimal;
    use serde_json::json;
    use uuid::Uuid;

    use super::*;
    use crate::context::{
        ActiveAlertSnapshot, PredictedPoint, PumpStateSnapshot, SensorContext, StartedSpan,
        StateSpanSnapshot, TempBasalSnapshot,
    };
    use crate::enums::{GlucoseBucket, PumpMode, StateSpanCategory, TrendBucket};
    use crate::eval::{Env, eval_payload};
    use crate::model::Payload;
    use crate::sustained::TimerStore;
    use crate::wall_clock::references_wall_clock;
    use ContextFact as F;

    const ALERT_ID: Uuid = Uuid::from_u128(1);
    const TRACKER_ID: Uuid = Uuid::from_u128(2);

    fn body(condition_type: &str, condition_params: &Value) -> Vec<ContextFact> {
        rule_reads(&RuleConditions::body(condition_type, condition_params))
    }

    fn with_auto_resolve<'a>(
        condition_type: &'a str,
        condition_params: &'a Value,
        auto_resolve_enabled: bool,
        auto_resolve_params: Option<&'a Value>,
    ) -> RuleConditions<'a> {
        RuleConditions {
            condition_type,
            condition_params,
            auto_resolve_enabled,
            auto_resolve_params,
        }
    }

    fn is_container(kind: ConditionKind) -> bool {
        matches!(
            kind,
            ConditionKind::Composite | ConditionKind::Not | ConditionKind::Sustained
        )
    }

    #[test]
    fn reads_names_the_facts_each_evaluator_reads() {
        assert_eq!(reads(ConditionKind::Threshold), &[F::Glucose]);
        assert_eq!(reads(ConditionKind::SignalLoss), &[F::Glucose]);
        assert_eq!(reads(ConditionKind::Predicted), &[F::Predictions]);
        assert_eq!(reads(ConditionKind::TempBasal), &[F::Insulin]);
        assert_eq!(reads(ConditionKind::TimeSinceLastCarb), &[F::Carbs]);
        assert_eq!(
            reads(ConditionKind::PumpSuspended),
            &[F::Pump, F::StateSpans]
        );
        assert_eq!(reads(ConditionKind::LoopEnactionStale), &[F::Aps]);
        assert_eq!(reads(ConditionKind::TimeOfDay), &[F::TimeZone]);
        assert!(reads(ConditionKind::Sustained).is_empty());
    }

    #[test]
    fn every_leaf_kind_reads_a_fact_and_no_container_does() {
        for &kind in ConditionKind::ALL {
            assert_eq!(
                reads(kind).is_empty(),
                is_container(kind),
                "{kind:?} reads {:?}",
                reads(kind)
            );
        }
    }

    #[test]
    fn every_fact_is_read_by_some_kind() {
        for &fact in ContextFact::ALL {
            assert!(
                ConditionKind::ALL
                    .iter()
                    .any(|&kind| reads(kind).contains(&fact)),
                "no kind reads {fact:?}"
            );
        }
    }

    #[test]
    fn wire_names_are_snake_case_and_round_trip() {
        for &fact in ContextFact::ALL {
            let name = fact.name();
            assert!(
                name.bytes().all(|b| b.is_ascii_lowercase() || b == b'_'),
                "{name}"
            );
            assert_eq!(ContextFact::from_name(name), Some(fact));
        }
    }

    #[test]
    fn a_leaf_root_is_selected_by_its_own_facts_whatever_its_body() {
        let threshold = json!({"direction": "below", "value": 70});
        assert_eq!(body("threshold", &threshold), [F::Glucose]);
        assert_eq!(body("THRESHOLD", &threshold), []);
        assert_eq!(body("tracker_age", &json!("not an object")), [F::Trackers]);
        let rule = RuleConditions::body("threshold", &threshold);
        assert!(references_any(&rule, &[F::Carbs, F::Glucose]));
        assert!(!references_any(&rule, &[F::Insulin]));
    }

    #[test]
    fn a_root_that_is_not_a_wire_name_reads_nothing() {
        let rate = json!({"direction": "falling", "rate": 2});
        assert!(body("RATE_OF_CHANGE", &rate).is_empty());
        assert!(body("RateOfChange", &rate).is_empty());
        assert!(body("1", &rate).is_empty());
        assert!(body("no_such_kind", &json!({})).is_empty());
    }

    #[test]
    fn a_nested_leaf_is_read() {
        let tree = json!({
            "operator": "and",
            "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "sustained", "sustained": { "minutes": 15, "child":
                    { "type": "iob", "iob": { "operator": ">", "value": 2 } } } }
            ]
        });
        assert_eq!(body("composite", &tree), [F::Glucose, F::Insulin]);
    }

    #[test]
    fn a_group_under_not_is_read() {
        let tree = json!({ "child": {
            "type": "composite",
            "composite": { "operator": "or", "conditions": [
                { "type": "sleep_session_active", "sleep_session_active": { "is_active": true } },
                { "type": "signal_loss", "signal_loss": { "timeout_minutes": 15 } }
            ] }
        } });
        let rule = RuleConditions::body("not", &tree);
        assert_eq!(rule_reads(&rule), [F::Glucose, F::Sleep]);
        assert!(references_wall_clock(&rule));
    }

    #[test]
    fn a_null_slot_makes_the_body_unevaluable() {
        let tree = json!({
            "operator": "or",
            "conditions": [ null, { "type": "staleness", "staleness": { "operator": ">", "value": 10 } } ]
        });
        assert!(parse_payload(ConditionKind::Composite, &tree).is_err());
        let rule = RuleConditions::body("composite", &tree);
        assert!(rule_reads(&rule).is_empty());
        assert!(!references_wall_clock(&rule));
    }

    #[test]
    fn a_container_without_its_payload_makes_the_body_unevaluable() {
        let tree = json!({
            "operator": "or",
            "conditions": [ { "type": "not" }, { "type": "sustained" } ]
        });
        assert!(parse_payload(ConditionKind::Composite, &tree).is_err());
        let rule = RuleConditions::body("composite", &tree);
        assert!(rule_reads(&rule).is_empty());
        assert!(!references_wall_clock(&rule));
    }

    #[test]
    fn an_unevaluable_sibling_hides_a_matching_leaf() {
        let tree = json!({
            "operator": "and",
            "conditions": [
                { "type": "signal_loss", "signal_loss": { "timeout_minutes": 15 } },
                { "type": "composite", "composite": { "operator": "and" } }
            ]
        });
        let rule = RuleConditions::body("composite", &tree);
        assert!(rule_reads(&rule).is_empty());
        assert!(!references_any(&rule, ContextFact::ALL));
        assert!(!references_wall_clock(&rule));
    }

    #[test]
    fn an_unevaluable_body_or_unknown_leaf_reads_nothing() {
        assert!(body("composite", &json!("not an object")).is_empty());
        let tree = json!({ "operator": "or", "conditions": [ { "type": "no_such_kind" } ] });
        assert!(body("composite", &tree).is_empty());
    }

    #[test]
    fn no_facts_selects_nothing() {
        let threshold = json!({"direction": "below", "value": 70});
        assert!(!references_any(
            &RuleConditions::body("threshold", &threshold),
            &[]
        ));
    }

    #[test]
    fn an_enabled_auto_resolve_tree_adds_its_facts() {
        let threshold = json!({"direction": "above", "value": 180});
        let cob_zero = json!({ "type": "cob", "cob": { "operator": "==", "value": 0 } });
        let rule = with_auto_resolve("threshold", &threshold, true, Some(&cob_zero));
        assert_eq!(rule_reads(&rule), [F::Glucose, F::Carbs]);
        assert!(references_any(&rule, &[F::Carbs]));
        assert!(!references_wall_clock(&rule));
    }

    #[test]
    fn an_enabled_auto_resolve_tree_can_make_a_rule_wall_clock() {
        let threshold = json!({"direction": "above", "value": 180});
        let stale = json!({ "type": "not", "not": { "child":
            { "type": "staleness", "staleness": { "operator": ">", "value": 30 } } } });
        let rule = with_auto_resolve("threshold", &threshold, true, Some(&stale));
        assert!(references_wall_clock(&rule));
    }

    #[test]
    fn a_disabled_or_unusable_auto_resolve_tree_is_ignored() {
        let threshold = json!({"direction": "above", "value": 180});
        let stale = json!({ "type": "staleness", "staleness": { "operator": ">", "value": 30 } });
        let null = Value::Null;
        let broken = json!({ "type": "composite", "composite": { "operator": "and" } });
        for rule in [
            with_auto_resolve("threshold", &threshold, false, Some(&stale)),
            with_auto_resolve("threshold", &threshold, true, None),
            with_auto_resolve("threshold", &threshold, true, Some(&null)),
            with_auto_resolve("threshold", &threshold, true, Some(&broken)),
        ] {
            assert_eq!(rule_reads(&rule), [F::Glucose], "{rule:?}");
            assert!(!references_wall_clock(&rule), "{rule:?}");
        }
    }

    #[test]
    fn a_wire_rule_carries_its_auto_resolve_tree() {
        let rule: WireRule = serde_json::from_value(json!({
            "id": Uuid::nil(),
            "condition_type": "threshold",
            "condition_params": { "direction": "above", "value": 180 },
            "auto_resolve_enabled": true,
            "auto_resolve_params": { "type": "iob", "iob": { "operator": "<", "value": 1 } }
        }))
        .expect("wire rule");
        assert_eq!(
            rule_reads(&RuleConditions::from(&rule)),
            [F::Glucose, F::Insulin]
        );
    }

    // Honesty: a kind's result does not move when any fact it does not read
    // moves. A kind that fails this reads a fact `reads` does not name.

    fn now() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-01-05T14:00:00Z")
            .expect("instant")
            .with_timezone(&Utc)
    }

    fn ago(minutes: i64) -> DateTime<Utc> {
        now()
            .checked_sub_signed(Duration::minutes(minutes))
            .expect("in range")
    }

    /// A context in which exactly the facts in `on` are populated. The struct
    /// literal has no `..` so a new field must be placed in a fact here.
    fn context(on: &[ContextFact]) -> SensorContext {
        let has = |fact: ContextFact| on.contains(&fact);
        let span = |fact| {
            has(fact).then(|| StartedSpan {
                started_at: ago(30),
            })
        };
        let (glucose, insulin, carbs, pump, aps, uploader, spans) = (
            has(F::Glucose),
            has(F::Insulin),
            has(F::Carbs),
            has(F::Pump),
            has(F::Aps),
            has(F::Uploader),
            has(F::StateSpans),
        );
        SensorContext {
            latest_value: glucose.then(|| Decimal::from(60)),
            latest_timestamp: glucose.then(|| ago(20)),
            trend_rate: glucose.then(|| Decimal::from(-3)),
            last_reading_at: glucose.then(|| ago(20)),
            trend_bucket: glucose.then_some(TrendBucket::FallingFast),
            glucose_bucket: glucose.then_some(GlucoseBucket::Low),
            predictions: if has(F::Predictions) {
                vec![PredictedPoint {
                    offset_minutes: 10,
                    mgdl: Decimal::from(50),
                }]
            } else {
                Vec::new()
            },
            iob_units: insulin.then(|| Decimal::from(3)),
            last_bolus_at: insulin.then(|| ago(30)),
            active_temp_basal: insulin.then(|| TempBasalSnapshot {
                rate: Decimal::from(2),
                scheduled_rate: Some(Decimal::ONE),
                started_at: ago(30),
            }),
            cob_grams: carbs.then(|| Decimal::from(20)),
            last_carb_at: carbs.then(|| ago(30)),
            reservoir_units: pump.then(|| Decimal::from(10)),
            reservoir_is_lower_bound: pump,
            pump_battery_percent: pump.then(|| Decimal::from(15)),
            has_ever_pump_snapshot: pump,
            last_aps_cycle_at: aps.then(|| ago(30)),
            last_aps_enacted_at: aps.then(|| ago(30)),
            has_ever_aps_cycled: aps,
            sensitivity_ratio: aps.then(|| Decimal::new(12, 1)),
            has_ever_aps_sensitivity: aps,
            uploader_battery_percent: uploader.then(|| Decimal::from(15)),
            has_ever_uploader_snapshot: uploader,
            active_override: span(F::StateSpans),
            active_pump_suspension: span(F::StateSpans),
            active_pump_state: spans.then(|| PumpStateSnapshot {
                mode: PumpMode::Exercise,
                started_at: ago(30),
            }),
            active_state_spans: if spans {
                HashMap::from([(
                    (StateSpanCategory::Illness, None),
                    StateSpanSnapshot {
                        started_at: ago(30),
                    },
                )])
            } else {
                HashMap::new()
            },
            last_site_change_at: has(F::DeviceAges).then(|| ago(80 * 60)),
            last_sensor_start_at: has(F::DeviceAges).then(|| ago(12 * 24 * 60)),
            active_trackers: if has(F::Trackers) {
                HashMap::from([(TRACKER_ID, ago(30))])
            } else {
                HashMap::new()
            },
            sleep_session_active: has(F::Sleep),
            active_alerts: if has(F::ActiveAlerts) {
                HashMap::from([(
                    ALERT_ID,
                    ActiveAlertSnapshot {
                        state: "firing".into(),
                        triggered_at: ago(30),
                        acknowledged_at: None,
                    },
                )])
            } else {
                HashMap::new()
            },
            active_do_not_disturb: span(F::DoNotDisturb),
            tenant_time_zone_id: has(F::TimeZone).then(|| "Australia/Sydney".to_owned()),
        }
    }

    /// A payload that is true in `context(reads(kind))` and false in
    /// `context(&[])`; for a container, one over a `threshold`.
    fn sample(kind: ConditionKind) -> Payload {
        let low = json!({"type": "threshold", "threshold": {"direction": "below", "value": 70}});
        let params = match kind {
            ConditionKind::Composite => json!({"operator": "and", "conditions": [low]}),
            ConditionKind::Not => json!({"child": low}),
            ConditionKind::Sustained => json!({"minutes": 5, "child": low}),
            ConditionKind::Threshold => json!({"direction": "below", "value": 70}),
            ConditionKind::RateOfChange => json!({"direction": "falling", "rate": 2}),
            ConditionKind::SignalLoss => json!({"timeout_minutes": 15}),
            ConditionKind::Staleness => json!({"operator": ">", "value": 10}),
            ConditionKind::Predicted => {
                json!({"operator": "<", "value": 55, "within_minutes": 30})
            }
            ConditionKind::Trend => json!({"bucket": "falling_fast"}),
            ConditionKind::TimeOfDay => json!({"from": "00:30", "to": "02:00"}),
            ConditionKind::Iob | ConditionKind::Cob => json!({"operator": ">", "value": 2}),
            ConditionKind::Reservoir => json!({"operator": ">", "value": 5}),
            ConditionKind::SiteAge => json!({"operator": ">", "value": 72}),
            ConditionKind::SensorAge => json!({"operator": ">", "value": 10}),
            ConditionKind::AlertState => json!({"alert_id": ALERT_ID, "state": "firing"}),
            ConditionKind::LoopStale | ConditionKind::LoopEnactionStale => {
                json!({"operator": ">", "minutes": 15})
            }
            ConditionKind::PumpSuspended
            | ConditionKind::OverrideActive
            | ConditionKind::DoNotDisturb
            | ConditionKind::SleepSessionActive => json!({"is_active": true}),
            ConditionKind::PumpBattery | ConditionKind::UploaderBattery => {
                json!({"operator": "<", "value": 20})
            }
            ConditionKind::TempBasal => json!({"metric": "rate", "operator": ">", "value": 1}),
            ConditionKind::SensitivityRatio => json!({"operator": ">", "value": 1}),
            ConditionKind::GlucoseBucket => json!({"buckets": ["low"]}),
            ConditionKind::TimeSinceLastCarb | ConditionKind::TimeSinceLastBolus => {
                json!({"operator": "<", "minutes": 60})
            }
            ConditionKind::DayOfWeek => json!({"days": ["tuesday"]}),
            ConditionKind::PumpState => json!({"mode": "Exercise", "is_active": true}),
            ConditionKind::StateSpanActive => json!({"category": "Illness", "is_active": true}),
            ConditionKind::TrackerAge => {
                json!({"tracker_definition_id": TRACKER_ID, "operator": ">", "minutes": 15})
            }
        };
        parse_payload(kind, &params).expect("sample payload parses")
    }

    fn evaluate(kind: ConditionKind, on: &[ContextFact]) -> bool {
        let ctx = context(on);
        let mut timers = TimerStore::default();
        let mut env = Env::new(now(), Uuid::nil(), &ctx, &mut timers);
        eval_payload(&sample(kind), kind.name(), &mut env)
    }

    #[test]
    fn every_leaf_sample_turns_on_its_own_facts() {
        for &kind in ConditionKind::ALL.iter().filter(|&&k| !is_container(k)) {
            assert!(evaluate(kind, reads(kind)), "{kind:?} with its facts");
            assert!(!evaluate(kind, &[]), "{kind:?} with no facts");
        }
    }

    #[test]
    fn every_fact_a_leaf_kind_reads_is_needed() {
        for &kind in ConditionKind::ALL.iter().filter(|&&k| !is_container(k)) {
            for &fact in reads(kind) {
                let without: Vec<ContextFact> = ContextFact::ALL
                    .iter()
                    .copied()
                    .filter(|&f| f != fact)
                    .collect();
                assert!(!evaluate(kind, &without), "{kind:?} without {fact:?}");
            }
        }
    }

    #[test]
    fn no_leaf_kind_moves_with_a_fact_it_does_not_read() {
        for &kind in ConditionKind::ALL.iter().filter(|&&k| !is_container(k)) {
            let own = reads(kind);
            let others: Vec<ContextFact> = ContextFact::ALL
                .iter()
                .copied()
                .filter(|f| !own.contains(f))
                .collect();
            assert_eq!(
                evaluate(kind, own),
                evaluate(kind, ContextFact::ALL),
                "{kind:?} moved with the facts it does not read"
            );
            for base in [Vec::new(), own.to_vec(), ContextFact::ALL.to_vec()] {
                let expected = evaluate(kind, &base);
                for &fact in &others {
                    let toggled: Vec<ContextFact> = if base.contains(&fact) {
                        base.iter().copied().filter(|&f| f != fact).collect()
                    } else {
                        base.iter().copied().chain([fact]).collect()
                    };
                    assert_eq!(
                        evaluate(kind, &toggled),
                        expected,
                        "{kind:?} moved with {fact:?}"
                    );
                }
            }
        }
    }
}
