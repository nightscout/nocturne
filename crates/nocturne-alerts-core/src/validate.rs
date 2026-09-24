//! Evaluability and save-time checks over parsed condition trees
//! (engine-semantics.md §1.4).
//!
//! Two tiers share one walk. Problems whose [`Reason::fails_evaluation`] is
//! true make evaluation fail, so parsing rejects them and the rule is
//! skipped. The rest leave a rule that evaluates but can never mean what its
//! author wrote (a node that is always false or always true); stored rules
//! keep evaluating them, and saving a rule rejects them. Some of those need
//! the JSON as written, not only as parsed: which properties are present and
//! which are not. The save tier walks it alongside the parsed tree.

use serde_json::{Map, Value};

use crate::enums::{EnumValue, Spelled, StateSpanCategory, WireEnum};
use crate::eval::clock::parse_hh_mm;
use crate::model::{
    ConditionKind, Node, ParseError, Payload, Reason, get_ci, parse_payload_structure,
};
use crate::paths::node_child_path;

#[derive(Clone, Copy, PartialEq, Eq)]
enum Tier {
    Evaluation,
    Save,
}

/// The first problem, in pre-order, that makes evaluating the tree rooted at
/// `node` (whose path is `root`) fail.
pub(crate) fn first_evaluation_fault(node: &Node, root: &str) -> Option<ParseError> {
    let mut found = Vec::new();
    check_node(Some(node), None, root, Tier::Evaluation, &mut found);
    found.into_iter().next()
}

/// [`first_evaluation_fault`] for a rule body, rooted at its kind's wire name.
pub(crate) fn first_evaluation_fault_in_payload(payload: &Payload) -> Option<ParseError> {
    let mut found = Vec::new();
    check_payload(
        payload,
        None,
        payload.kind().name(),
        Tier::Evaluation,
        &mut found,
    );
    found.into_iter().next()
}

/// Every problem saving a rule body should reject: `condition_params` as
/// stored for `condition_type`. A structurally malformed payload reports only
/// its first error, as the reader stops there.
#[must_use]
pub fn validate_rule(condition_type: &str, condition_params: &Value) -> Vec<ParseError> {
    let Some(kind) =
        ConditionKind::from_name(condition_type).filter(|k| k.name() == condition_type)
    else {
        let reason = match ConditionKind::resolve(condition_type) {
            Some(_) => Reason::NonCanonicalType,
            None => Reason::UnknownKind,
        };
        return vec![ParseError::new(condition_type, reason)];
    };
    if condition_params.is_null() {
        return vec![ParseError::new(kind.name(), Reason::PayloadMissing)];
    }
    match parse_payload_structure(kind, condition_params) {
        Err(e) => vec![e],
        Ok(payload) => {
            let mut found = Vec::new();
            check_payload(
                &payload,
                condition_params.as_object(),
                kind.name(),
                Tier::Save,
                &mut found,
            );
            found
        }
    }
}

/// Every problem saving a full condition node should reject, with paths
/// rooted at `root` (`auto_resolve`, `snooze`, …).
#[must_use]
pub fn validate_node(node: &Value, root: &str) -> Vec<ParseError> {
    match Node::parse_structure_rooted(node, root) {
        Err(e) => vec![e],
        Ok(parsed) => {
            let mut found = Vec::new();
            check_node(
                Some(&parsed),
                node.as_object(),
                root,
                Tier::Save,
                &mut found,
            );
            found
        }
    }
}

fn report(found: &mut Vec<ParseError>, tier: Tier, path: &str, reason: Reason) {
    if tier == Tier::Save || reason.fails_evaluation() {
        found.push(ParseError::new(path, reason));
    }
}

/// `None` is a JSON-null composite slot. `raw` is the node's JSON object,
/// given only on the save tier.
fn check_node(
    node: Option<&Node>,
    raw: Option<&Map<String, Value>>,
    path: &str,
    tier: Tier,
    found: &mut Vec<ParseError>,
) {
    let Some(node) = node else {
        return report(found, tier, path, Reason::ConditionMissing);
    };
    if raw.is_some_and(|o| o.keys().any(|k| !is_node_property(k))) {
        report(found, tier, path, Reason::UnknownField);
    }
    let Some(type_str) = node.type_str.as_deref() else {
        return report(found, tier, path, Reason::TypeMissing);
    };
    let Some(payload) = node.dispatch() else {
        return report(found, tier, path, Reason::UnknownKind);
    };
    let wire = payload.kind().name();
    if type_str != wire {
        report(found, tier, path, Reason::NonCanonicalType);
    }
    // A kind reached through its member name or ordinal reads the default
    // payload, not the stored one, so no written payload is evaluated. An
    // absent or null payload is written as no properties at all.
    let empty = Map::new();
    let raw_payload = raw
        .filter(|_| type_str.eq_ignore_ascii_case(wire))
        .map(|o| get_ci(o, wire).and_then(Value::as_object).unwrap_or(&empty));
    check_payload(&payload, raw_payload, path, tier, found);
}

/// `type`, any kind's payload property (a node may carry payloads other than
/// the one its `type` names, which are parsed but not evaluated), or the web
/// rule editor's node key `_uid`, which rules it saved still carry.
fn is_node_property(name: &str) -> bool {
    name.eq_ignore_ascii_case("type") || name == "_uid" || ConditionKind::from_name(name).is_some()
}

fn check_child(
    child: &Node,
    raw: Option<&Value>,
    parent: &str,
    index: usize,
    tier: Tier,
    found: &mut Vec<ParseError>,
) {
    check_node(
        Some(child),
        raw.and_then(Value::as_object),
        &node_child_path(parent, index, Some(child)),
        tier,
        found,
    );
}

/// `raw` is the payload's JSON object as written, given only on the save
/// tier and only when evaluation reads it.
fn check_payload(
    payload: &Payload,
    raw: Option<&Map<String, Value>>,
    path: &str,
    tier: Tier,
    found: &mut Vec<ParseError>,
) {
    if let Some(raw) = raw {
        let fields = payload.fields();
        if raw
            .keys()
            .any(|k| !fields.iter().any(|f| f.eq_ignore_ascii_case(k)))
        {
            report(found, tier, path, Reason::UnknownField);
        }
        for &field in required_fields(payload) {
            if get_ci(raw, field).is_none_or(Value::is_null) {
                report(found, tier, path, Reason::FieldMissing(field));
            }
        }
    }
    let raw_field = |name: &str| raw.and_then(|o| get_ci(o, name));
    // An operand `required_fields` already reports missing is not also
    // reported for the default it reads as.
    let written = |name: &str| raw.is_none_or(|o| get_ci(o, name).is_some_and(|v| !v.is_null()));

    let problem = match payload {
        Payload::Composite(p) => {
            let Some(conditions) = &p.conditions else {
                return report(found, tier, path, Reason::ConditionsMissing);
            };
            if conditions.is_empty() {
                return report(found, tier, path, Reason::ConditionsEmpty);
            }
            if let Some(reason) = word_problem(
                &p.operator,
                Reason::OperatorMissing,
                Reason::UnknownOperator,
            ) {
                report(found, tier, path, reason);
            }
            let raw_conditions = raw_field("conditions").and_then(Value::as_array);
            for (i, child) in conditions.iter().enumerate() {
                let raw_child = raw_conditions.and_then(|c| c.get(i));
                match child {
                    Some(c) => check_child(c, raw_child, path, i, tier, found),
                    None => check_node(None, None, &node_child_path(path, i, None), tier, found),
                }
            }
            None
        }
        Payload::Not(p) => match &p.child {
            None => Some(Reason::ChildMissing),
            Some(c) => {
                check_child(c, raw_field("child"), path, 0, tier, found);
                None
            }
        },
        Payload::Sustained(p) => {
            if p.minutes <= 0 {
                report(found, tier, path, Reason::MinutesNotPositive("minutes"));
            }
            match &p.child {
                None => Some(Reason::ChildMissing),
                Some(c) => {
                    check_child(c, raw_field("child"), path, 0, tier, found);
                    None
                }
            }
        }
        Payload::Threshold(p) => word_problem(
            &p.direction,
            Reason::DirectionMissing,
            Reason::UnknownDirection,
        ),
        Payload::RateOfChange(p) => word_problem(
            &p.direction,
            Reason::DirectionMissing,
            Reason::UnknownDirection,
        ),
        Payload::AlertState(p) => {
            word_problem(&p.state, Reason::StateMissing, Reason::UnknownState)
        }
        Payload::Iob(p)
        | Payload::Cob(p)
        | Payload::Reservoir(p)
        | Payload::SiteAge(p)
        | Payload::SensorAge(p)
        | Payload::PumpBattery(p)
        | Payload::UploaderBattery(p)
        | Payload::SensitivityRatio(p) => operator_problem(&p.operator),
        Payload::LoopStale(p) | Payload::LoopEnactionStale(p) => {
            negative(p.minutes, "minutes", tier, path, found);
            operator_problem(&p.operator)
        }
        Payload::Staleness(p) => {
            negative(p.value, "value", tier, path, found);
            operator_problem(&p.operator)
        }
        Payload::Predicted(p) => {
            if p.within_minutes <= 0 && written("within_minutes") {
                report(
                    found,
                    tier,
                    path,
                    Reason::MinutesNotPositive("within_minutes"),
                );
            }
            operator_problem(&p.operator)
        }
        Payload::TrackerAge(p) => operator_problem(&p.operator),
        Payload::TempBasal(p) => {
            undefined(p.metric, "metric", tier, path, found);
            operator_problem(&p.operator)
        }
        Payload::TimeSinceLastCarb(p) | Payload::TimeSinceLastBolus(p) => {
            negative(p.minutes, "minutes", tier, path, found);
            matches!(p.operator, EnumValue::Undefined(_)).then_some(Reason::UnknownOperator)
        }
        Payload::Trend(p) => word_problem(
            &p.bucket,
            Reason::FieldMissing("bucket"),
            Reason::UnknownValue("bucket"),
        ),
        Payload::TimeOfDay(p) => {
            let from = time_bound(p.from.as_deref(), "from", tier, path, found);
            let to = time_bound(p.to.as_deref(), "to", tier, path, found);
            (from.is_some() && from == to).then_some(Reason::EmptyWindow)
        }
        Payload::GlucoseBucket(p) => list_problem(p.buckets.as_deref(), "buckets"),
        Payload::DayOfWeek(p) => list_problem(p.days.as_deref(), "days"),
        Payload::PumpState(p) => {
            undefined(p.mode, "mode", tier, path, found);
            None
        }
        Payload::StateSpanActive(p) => match p.category {
            EnumValue::Known(StateSpanCategory::PumpMode) => Some(Reason::PumpModeCategory),
            EnumValue::Undefined(_) => Some(Reason::UnknownValue("category")),
            EnumValue::Known(_) => None,
        },
        Payload::SignalLoss(p) => (p.timeout_minutes <= 0 && written("timeout_minutes"))
            .then_some(Reason::MinutesNotPositive("timeout_minutes")),
        Payload::PumpSuspended(_)
        | Payload::OverrideActive(_)
        | Payload::DoNotDisturb(_)
        | Payload::SleepSessionActive(_) => None,
    };
    if let Some(reason) = problem {
        report(found, tier, path, reason);
    }
}

/// The operands a payload reads as a default when absent where that default
/// is not an authored value, and no other reason reports them missing.
fn required_fields(payload: &Payload) -> &'static [&'static str] {
    match payload {
        Payload::Threshold(_)
        | Payload::Staleness(_)
        | Payload::Iob(_)
        | Payload::Cob(_)
        | Payload::Reservoir(_)
        | Payload::SiteAge(_)
        | Payload::SensorAge(_)
        | Payload::PumpBattery(_)
        | Payload::UploaderBattery(_)
        | Payload::SensitivityRatio(_) => &["value"],
        Payload::RateOfChange(_) => &["rate"],
        Payload::SignalLoss(_) => &["timeout_minutes"],
        Payload::Predicted(_) => &["value", "within_minutes"],
        Payload::AlertState(_) => &["alert_id"],
        Payload::LoopStale(_) | Payload::LoopEnactionStale(_) => &["minutes"],
        Payload::PumpSuspended(_)
        | Payload::OverrideActive(_)
        | Payload::DoNotDisturb(_)
        | Payload::SleepSessionActive(_) => &["is_active"],
        Payload::TempBasal(_) => &["metric", "value"],
        Payload::TimeSinceLastCarb(_) | Payload::TimeSinceLastBolus(_) => &["operator", "minutes"],
        Payload::PumpState(_) => &["mode", "is_active"],
        Payload::StateSpanActive(_) => &["category", "is_active"],
        Payload::TrackerAge(_) => &["tracker_definition_id", "minutes"],
        Payload::Composite(_)
        | Payload::Not(_)
        | Payload::Sustained(_)
        | Payload::Trend(_)
        | Payload::TimeOfDay(_)
        | Payload::GlucoseBucket(_)
        | Payload::DayOfWeek(_) => &[],
    }
}

fn word_problem<T>(word: &Spelled<T>, missing: Reason, unknown: Reason) -> Option<Reason> {
    match (&word.text, &word.value) {
        (None, _) => Some(missing),
        (Some(_), None) => Some(unknown),
        (Some(_), Some(_)) => None,
    }
}

/// A missing comparison operator compares false, like an unknown one.
fn operator_problem<T>(operator: &Spelled<T>) -> Option<Reason> {
    operator.value.is_none().then_some(Reason::UnknownOperator)
}

/// An elapsed time is never negative, and an absent anchor reads as an
/// infinite one, so a negative bound compares the same way every tick.
fn negative(bound: i32, field: &'static str, tier: Tier, path: &str, found: &mut Vec<ParseError>) {
    if bound < 0 {
        report(found, tier, path, Reason::MinutesNegative(field));
    }
}

fn undefined<E>(
    value: EnumValue<E>,
    field: &'static str,
    tier: Tier,
    path: &str,
    found: &mut Vec<ParseError>,
) {
    if matches!(value, EnumValue::Undefined(_)) {
        report(found, tier, path, Reason::UnknownValue(field));
    }
}

/// An absent or empty list matches nothing, and so does an undefined member.
fn list_problem<E>(list: Option<&[EnumValue<E>]>, field: &'static str) -> Option<Reason> {
    match list {
        None | Some([]) => Some(Reason::ListEmpty(field)),
        Some(items) => items
            .iter()
            .any(|v| matches!(v, EnumValue::Undefined(_)))
            .then_some(Reason::UnknownValue(field)),
    }
}

/// The bound as a time, reporting it when it is absent or not `HH:mm`.
fn time_bound(
    bound: Option<&str>,
    field: &'static str,
    tier: Tier,
    path: &str,
    found: &mut Vec<ParseError>,
) -> Option<chrono::NaiveTime> {
    let Some(text) = bound else {
        report(found, tier, path, Reason::FieldMissing(field));
        return None;
    };
    let time = parse_hh_mm(text);
    if time.is_none() {
        report(found, tier, path, Reason::InvalidTime(field));
    }
    time
}
