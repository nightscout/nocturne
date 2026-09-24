//! Wall-clock-sensitive condition kinds (`docs/alerts/engine-semantics.md`
//! §5.1): kinds whose truth can change while every fact except `now` stands
//! still. A host must evaluate a rule that contains one on a timer as well as
//! per reading, or it can stay unfired for as long as no reading arrives.

use serde_json::Value;

use crate::leaf_identity::collect_leaves;
use crate::model::{ConditionKind, Node, parse_payload};

/// Whether `kind` measures elapsed time against an anchor a reading does not
/// move. Containers are not: a `sustained` over a reading-driven child only
/// extrapolates the last reading. `time_of_day` and `day_of_week` gate
/// reading-driven leaves, so the next reading bounds their delay.
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
        | ConditionKind::SensitivityRatio
        | ConditionKind::GlucoseBucket
        | ConditionKind::DayOfWeek
        | ConditionKind::SleepSessionActive => false,
    }
}

/// Whether a stored rule's root kind, or any leaf of its tree, is wall-clock
/// sensitive. A root kind that does not resolve, or a container payload that
/// does not parse, is false: it cannot be evaluated either.
pub fn references_wall_clock(condition_type: &str, condition_params: &Value) -> bool {
    let Some(kind) = ConditionKind::resolve(condition_type) else {
        return false;
    };
    if is_wall_clock(kind) {
        return true;
    }
    let Ok(payload) = parse_payload(kind, condition_params) else {
        return false;
    };
    let root = Node::from_rule(kind, Some(payload));
    collect_leaves(&root).into_iter().flatten().any(|leaf| {
        leaf.type_str
            .as_deref()
            .and_then(ConditionKind::resolve)
            .is_some_and(is_wall_clock)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn manifest_names(key: &str) -> Vec<String> {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../tests/Parity/AlertEngineEnums.json"
        );
        let text = std::fs::read_to_string(path).expect("read enum manifest");
        let manifest: Value = serde_json::from_str(&text).expect("parse enum manifest");
        manifest[key]
            .as_array()
            .unwrap_or_else(|| panic!("{key} missing from manifest"))
            .iter()
            .map(|v| v.as_str().expect("kind is a string").to_string())
            .collect()
    }

    #[test]
    fn wall_clock_kinds_match_the_manifest() {
        let ours: Vec<String> = manifest_names("AlertConditionType")
            .into_iter()
            .filter(|wire| {
                ConditionKind::from_wire(wire)
                    .map(is_wall_clock)
                    .expect("manifest kind resolves")
            })
            .collect();
        assert_eq!(
            ours,
            manifest_names("WallClockConditionTypes"),
            "wall-clock kinds drifted from C#"
        );
    }

    #[test]
    fn wall_clock_root_is_selected_whatever_its_payload() {
        assert!(references_wall_clock(
            "signal_loss",
            &json!({"timeout_minutes": 15})
        ));
        assert!(references_wall_clock(
            "tracker_age",
            &json!("not an object")
        ));
    }

    #[test]
    fn reading_driven_root_is_not_selected() {
        assert!(!references_wall_clock(
            "threshold",
            &json!({"direction": "below", "value": 70})
        ));
        assert!(!references_wall_clock("no_such_kind", &json!({})));
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
                        "type": "SignalLoss", "signal_loss": { "timeout_minutes": 15 }
                    } }
                } } }
            ]
        });
        assert!(references_wall_clock("composite", &tree));
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
        assert!(!references_wall_clock("composite", &tree));
    }
}
