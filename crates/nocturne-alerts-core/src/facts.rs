//! The context facts each condition kind reads (`docs/alerts/engine-semantics.md`
//! §5.2). A host that knows which facts a write touched need evaluate only the
//! rules that read one of them.

use serde_json::Value;

use crate::leaf_identity::rule_references;
use crate::model::ConditionKind;

/// A group of [`SensorContext`](crate::context::SensorContext) fields that a
/// host fills from one kind of record. `now` is not a fact: no write moves it,
/// and [`is_wall_clock`] names the kinds it drives.
///
/// [`is_wall_clock`]: crate::wall_clock::is_wall_clock
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ContextFact {
    /// `latest_value`, `latest_timestamp`, `last_reading_at`, `trend_rate`,
    /// `trend_bucket`, and `glucose_bucket`, which the host resolves against
    /// the target range.
    Glucose,
    /// `predictions`.
    Predictions,
    /// `iob_units`, `last_bolus_at`, `active_temp_basal`.
    Insulin,
    /// `cob_grams`, `last_carb_at`.
    Carbs,
    /// `reservoir_units`, `reservoir_is_lower_bound`, `pump_battery_percent`,
    /// `has_ever_pump_snapshot`.
    Pump,
    /// `last_aps_cycle_at`, `last_aps_enacted_at`, `has_ever_aps_cycled`,
    /// `sensitivity_ratio`, `has_ever_aps_sensitivity`.
    Aps,
    /// `uploader_battery_percent`, `has_ever_uploader_snapshot`.
    Uploader,
    /// `active_override`, `active_pump_suspension`, `active_pump_state`,
    /// `active_state_spans`.
    StateSpans,
    /// `last_site_change_at`, `last_sensor_start_at`.
    DeviceAges,
    /// `active_trackers`.
    Trackers,
    /// `sleep_session_active`.
    Sleep,
    /// `active_alerts`.
    ActiveAlerts,
    /// `active_do_not_disturb`.
    DoNotDisturb,
    /// `tenant_time_zone_id`.
    TimeZone,
}

/// The facts `kind`'s own evaluator reads. A container reads none; the facts
/// beneath it are its children's ([`references_any`]).
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

/// Whether a stored rule's root kind, or any leaf of its tree, reads one of
/// `facts`. The rule is read as
/// [`references_wall_clock`](crate::wall_clock::references_wall_clock) reads
/// it, so an unknown root kind or an unevaluable body is false.
#[must_use]
pub fn references_any(
    condition_type: &str,
    condition_params: &Value,
    facts: &[ContextFact],
) -> bool {
    rule_references(condition_type, condition_params, |kind| {
        reads(kind).iter().any(|fact| facts.contains(fact))
    })
}

#[cfg(test)]
mod tests {
    use std::collections::HashSet;

    use super::*;
    use crate::enums::WireEnum;
    use crate::wall_clock::is_wall_clock;
    use ContextFact as F;
    use serde_json::json;

    const EVERY_FACT: [ContextFact; 14] = [
        F::Glucose,
        F::Predictions,
        F::Insulin,
        F::Carbs,
        F::Pump,
        F::Aps,
        F::Uploader,
        F::StateSpans,
        F::DeviceAges,
        F::Trackers,
        F::Sleep,
        F::ActiveAlerts,
        F::DoNotDisturb,
        F::TimeZone,
    ];

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
    fn a_wall_clock_kind_still_reads_the_fact_holding_its_anchor() {
        for &kind in ConditionKind::ALL.iter().filter(|&&k| is_wall_clock(k)) {
            assert!(!reads(kind).is_empty(), "{kind:?} reads no fact");
        }
    }

    #[test]
    fn every_fact_is_read_by_some_kind() {
        let read: HashSet<ContextFact> = ConditionKind::ALL
            .iter()
            .flat_map(|&kind| reads(kind).iter().copied())
            .collect();
        assert_eq!(read, HashSet::from(EVERY_FACT));
    }

    #[test]
    fn a_leaf_root_is_selected_by_its_own_facts() {
        let body = json!({"direction": "below", "value": 70});
        assert!(references_any("threshold", &body, &[F::Glucose]));
        assert!(references_any("THRESHOLD", &body, &[F::Carbs, F::Glucose]));
        assert!(!references_any("threshold", &body, &[F::Insulin]));
        assert!(references_any(
            "tracker_age",
            &json!("not an object"),
            &[F::Trackers]
        ));
    }

    #[test]
    fn a_root_that_is_not_a_wire_name_is_not_selected() {
        let body = json!({"direction": "falling", "rate": 2});
        assert!(references_any("RATE_OF_CHANGE", &body, &EVERY_FACT));
        assert!(!references_any("RateOfChange", &body, &EVERY_FACT));
        assert!(!references_any("1", &body, &EVERY_FACT));
        assert!(!references_any("no_such_kind", &json!({}), &EVERY_FACT));
    }

    #[test]
    fn a_nested_leaf_is_selected() {
        let tree = json!({
            "operator": "and",
            "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "sustained", "sustained": { "minutes": 15, "child":
                    { "type": "Iob", "iob": { "operator": ">", "value": 2 } } } }
            ]
        });
        assert!(references_any("composite", &tree, &[F::Insulin]));
        assert!(references_any("composite", &tree, &[F::Glucose]));
        assert!(!references_any("composite", &tree, &[F::Carbs, F::Pump]));
    }

    #[test]
    fn a_leaf_under_not_is_selected() {
        let body = json!({ "child": {
            "type": "sleep_session_active", "sleep_session_active": { "is_active": true }
        } });
        assert!(references_any("not", &body, &[F::Sleep]));
        assert!(!references_any("not", &body, &[F::Glucose]));
    }

    #[test]
    fn an_unevaluable_body_or_unknown_leaf_is_not_selected() {
        assert!(!references_any(
            "composite",
            &json!("not an object"),
            &EVERY_FACT
        ));
        let tree = json!({
            "operator": "or",
            "conditions": [ { "type": "no_such_kind" } ]
        });
        assert!(!references_any("composite", &tree, &EVERY_FACT));
    }

    #[test]
    fn no_facts_selects_nothing() {
        let body = json!({"direction": "below", "value": 70});
        assert!(!references_any("threshold", &body, &[]));
        assert!(!references_any(
            "not",
            &json!({ "child": { "type": "threshold" } }),
            &[]
        ));
    }
}
