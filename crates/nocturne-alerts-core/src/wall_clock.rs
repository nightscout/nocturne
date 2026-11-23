//! Wall-clock-sensitive condition kinds (`docs/alerts/engine-semantics.md`
//! §5.1): kinds whose truth can change while every fact except `now` stands
//! still. A host must evaluate a rule that contains one on a timer as well as
//! per reading, or it can stay unfired for as long as no reading arrives.

use crate::engine::OnUnknown;
use crate::enums::LimitFact;
use crate::eval::glucose::judges_freshness;
use crate::eval::judges_determination_freshness;
use crate::facts::RuleConditions;
use crate::model::ConditionKind;

/// Whether `kind` measures elapsed time against an anchor a reading does not
/// move. Containers are not: a `sustained` over a reading-driven child only
/// extrapolates the last reading. `time_of_day` and `day_of_week` gate
/// reading-driven leaves, so the next reading bounds their delay.
#[must_use]
pub fn is_wall_clock(kind: ConditionKind) -> bool {
    match kind {
        ConditionKind::SignalLoss
        | ConditionKind::Staleness
        | ConditionKind::SiteAge
        | ConditionKind::SensorAge
        | ConditionKind::AlertState
        | ConditionKind::LoopStale
        | ConditionKind::LoopEnactionStale
        | ConditionKind::PumpSuspended
        | ConditionKind::OverrideActive
        | ConditionKind::DoNotDisturb
        | ConditionKind::TimeSinceLastCarb
        | ConditionKind::TimeSinceLastBolus
        | ConditionKind::PumpState
        | ConditionKind::StateSpanActive
        | ConditionKind::SensorState
        | ConditionKind::TrackerAge => true,
        ConditionKind::Threshold
        | ConditionKind::RateOfChange
        | ConditionKind::Composite
        | ConditionKind::Not
        | ConditionKind::Sustained
        | ConditionKind::Predicted
        | ConditionKind::Trend
        | ConditionKind::TimeOfDay
        | ConditionKind::Iob
        | ConditionKind::Cob
        | ConditionKind::Reservoir
        | ConditionKind::PumpBattery
        | ConditionKind::TempBasal
        | ConditionKind::UploaderBattery
        | ConditionKind::TransmitterBattery
        | ConditionKind::SensitivityRatio
        | ConditionKind::GlucoseBucket
        | ConditionKind::DayOfWeek
        | ConditionKind::SleepSessionActive
        | ConditionKind::EventualGlucose
        | ConditionKind::CarbsRequired => false,
    }
}

/// Whether any kind of `rule` ([`RuleConditions`]) is wall-clock sensitive,
/// or the rule reads an unknown root as known and has a kind, or a limit
/// naming `latest_glucose`, whose glucose fact goes stale with the clock
/// (engine-semantics.md §5.1). A wall-clock root is true whatever its body.
#[must_use]
pub fn references_wall_clock(rule: &RuleConditions<'_>) -> bool {
    let reads_unknown = rule.on_unknown != OnUnknown::Hold;
    rule.kinds().into_iter().any(|kind| {
        is_wall_clock(kind)
            || (reads_unknown && (judges_freshness(kind) || judges_determination_freshness(kind)))
    }) || (reads_unknown && rule.limit_facts().contains(&LimitFact::LatestGlucose))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::enums::WireEnum;
    use serde_json::{Value, json};

    fn body_references_wall_clock(condition_type: &str, condition_params: &Value) -> bool {
        references_wall_clock(&RuleConditions::body(condition_type, condition_params))
    }

    /// The §5.1 table. A kind dropped from it would only re-evaluate on the next
    /// reading, which during a CGM outage may never come.
    #[test]
    fn the_wall_clock_kinds_are_the_spec_table() {
        let mut ours: Vec<&str> = ConditionKind::ALL
            .iter()
            .copied()
            .filter(|&kind| is_wall_clock(kind))
            .map(ConditionKind::name)
            .collect();
        ours.sort_unstable();
        assert_eq!(
            ours,
            [
                "alert_state",
                "do_not_disturb",
                "loop_enaction_stale",
                "loop_stale",
                "override_active",
                "pump_state",
                "pump_suspended",
                "sensor_age",
                "sensor_state",
                "signal_loss",
                "site_age",
                "staleness",
                "state_span_active",
                "time_since_last_bolus",
                "time_since_last_carb",
                "tracker_age",
            ]
        );
    }

    #[test]
    fn wall_clock_root_is_selected_whatever_its_payload() {
        assert!(body_references_wall_clock(
            "signal_loss",
            &json!({"timeout_minutes": 15})
        ));
        assert!(body_references_wall_clock(
            "tracker_age",
            &json!("not an object")
        ));
    }

    #[test]
    fn reading_driven_root_is_not_selected() {
        assert!(!body_references_wall_clock(
            "threshold",
            &json!({"direction": "below", "value": 70})
        ));
        assert!(!body_references_wall_clock("no_such_kind", &json!({})));
    }

    #[test]
    fn a_root_that_is_not_a_wire_name_is_not_selected() {
        assert!(!body_references_wall_clock(
            "SIGNAL_LOSS",
            &json!({"timeout_minutes": 15})
        ));
        assert!(!body_references_wall_clock(
            "SignalLoss",
            &json!({"timeout_minutes": 15})
        ));
        assert!(!body_references_wall_clock(
            "2",
            &json!({"timeout_minutes": 15})
        ));
    }

    #[test]
    fn nested_wall_clock_leaf_is_selected() {
        let tree = json!({
            "operator": "and",
            "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "not", "not": { "child": {
                    "type": "sustained",
                    "sustained": { "minutes": 10, "child": {
                        "type": "signal_loss", "signal_loss": { "timeout_minutes": 15 }
                    } }
                } } }
            ]
        });
        assert!(body_references_wall_clock("composite", &tree));
    }

    /// A rule that reads an unknown root as known moves when its reading goes
    /// stale, so the staleness instant must be evaluated; under `hold` a
    /// missed evaluation there changes nothing (§5.1).
    #[test]
    fn a_freshness_leaf_is_selected_only_when_unknown_is_read_as_known() {
        let low = json!({"direction": "below", "value": 70});
        let iob = json!({"operator": ">", "value": 2});
        for (kind, params) in [
            ("threshold", &low),
            (
                "rate_of_change",
                &json!({"direction": "falling", "rate": 2}),
            ),
            ("trend", &json!({"bucket": "falling_fast"})),
            ("glucose_bucket", &json!({"buckets": ["low"]})),
        ] {
            for on_unknown in OnUnknown::ALL {
                let rule = RuleConditions {
                    on_unknown: *on_unknown,
                    ..RuleConditions::body(kind, params)
                };
                assert_eq!(
                    references_wall_clock(&rule),
                    *on_unknown != OnUnknown::Hold,
                    "{kind} {on_unknown:?}"
                );
            }
        }
        let not_glucose = RuleConditions {
            on_unknown: OnUnknown::NotMet,
            ..RuleConditions::body("iob", &iob)
        };
        assert!(!references_wall_clock(&not_glucose));
        let resolve = json!({"type": "threshold", "threshold": low});
        let in_resolve = RuleConditions {
            condition_type: "iob",
            condition_params: &iob,
            auto_resolve_enabled: true,
            auto_resolve_params: Some(&resolve),
            on_unknown: OnUnknown::Met,
        };
        assert!(references_wall_clock(&in_resolve));
    }

    #[test]
    fn reading_driven_tree_with_calendar_gates_is_not_selected() {
        let tree = json!({
            "operator": "and",
            "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "time_of_day", "time_of_day": { "from": "22:00", "to": "06:00" } },
                { "type": "sustained", "sustained": { "minutes": 15, "child":
                    { "type": "iob", "iob": { "operator": ">", "value": 2 } } } }
            ]
        });
        assert!(!body_references_wall_clock("composite", &tree));
    }
}
