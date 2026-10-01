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
    ConditionKind, Node, ParseError, Payload, Reason, get_ci, get_ci_entry, parse_payload_structure,
};
use crate::paths::node_child_path;

#[derive(Clone, Copy, PartialEq, Eq)]
enum Tier {
    Evaluation,
    Save,
}

/// A property the save check reports as [`Reason::UnknownField`]. Neither
/// engine reads it, so removing it cannot change what the rule does.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UnknownKey {
    /// Condition path of the node it is on.
    pub path: String,
    /// JSON Pointer (RFC 6901) to the object holding it, from the root of
    /// the value checked.
    pub pointer: String,
    /// The property name as written.
    pub key: String,
}

#[derive(Default)]
struct Found {
    issues: Vec<ParseError>,
    unknown: Vec<UnknownKey>,
}

/// A JSON object as written, with its [`UnknownKey::pointer`].
#[derive(Clone, Copy)]
struct Raw<'a> {
    obj: &'a Map<String, Value>,
    pointer: &'a str,
}

impl<'a> Raw<'a> {
    fn get(self, name: &str) -> Option<&'a Value> {
        get_ci(self.obj, name)
    }

    /// The pointer to the property `name` resolves to, and its value.
    fn entry(self, name: &str) -> Option<(String, &'a Value)> {
        get_ci_entry(self.obj, name).map(|(k, v)| (child_pointer(self.pointer, k), v))
    }

    fn report_unknown(
        self,
        known: impl Fn(&str) -> bool,
        tier: Tier,
        path: &str,
        found: &mut Found,
    ) {
        let unknown: Vec<UnknownKey> = self
            .obj
            .keys()
            .filter(|k| !known(k))
            .map(|k| UnknownKey {
                path: path.to_owned(),
                pointer: self.pointer.to_owned(),
                key: k.clone(),
            })
            .collect();
        if !unknown.is_empty() {
            report(found, tier, path, Reason::UnknownField);
            found.unknown.extend(unknown);
        }
    }
}

fn child_pointer(parent: &str, key: &str) -> String {
    format!("{parent}/{}", key.replace('~', "~0").replace('/', "~1"))
}

/// The first problem, in pre-order, that makes evaluating the tree rooted at
/// `node` (whose path is `root`) fail.
pub(crate) fn first_evaluation_fault(node: &Node, root: &str) -> Option<ParseError> {
    let mut found = Found::default();
    check_node(Some(node), None, root, Tier::Evaluation, &mut found);
    found.issues.into_iter().next()
}

/// [`first_evaluation_fault`] for a rule body, rooted at its kind's wire name.
pub(crate) fn first_evaluation_fault_in_payload(payload: &Payload) -> Option<ParseError> {
    let mut found = Found::default();
    check_payload(
        payload,
        None,
        payload.kind().name(),
        Tier::Evaluation,
        &mut found,
    );
    found.issues.into_iter().next()
}

/// Every problem saving a rule body should reject: `condition_params` as
/// stored for `condition_type`. A structurally malformed payload reports only
/// its first error, as the reader stops there.
#[must_use]
pub fn validate_rule(condition_type: &str, condition_params: &Value) -> Vec<ParseError> {
    check_rule(condition_type, condition_params).issues
}

/// The properties behind the [`Reason::UnknownField`] issues
/// [`validate_rule`] reports.
#[must_use]
pub fn unknown_keys_in_rule(condition_type: &str, condition_params: &Value) -> Vec<UnknownKey> {
    check_rule(condition_type, condition_params).unknown
}

fn check_rule(condition_type: &str, condition_params: &Value) -> Found {
    let issue = |path: &str, reason| Found {
        issues: vec![ParseError::new(path, reason)],
        unknown: Vec::new(),
    };
    let Some(kind) =
        ConditionKind::from_name(condition_type).filter(|k| k.name() == condition_type)
    else {
        let reason = match ConditionKind::resolve(condition_type) {
            Some(_) => Reason::NonCanonicalType,
            None => Reason::UnknownKind,
        };
        return issue(condition_type, reason);
    };
    if condition_params.is_null() {
        return issue(kind.name(), Reason::PayloadMissing);
    }
    match parse_payload_structure(kind, condition_params) {
        Err(e) => Found {
            issues: vec![e],
            unknown: Vec::new(),
        },
        Ok(payload) => {
            let mut found = Found::default();
            check_payload(
                &payload,
                condition_params
                    .as_object()
                    .map(|obj| Raw { obj, pointer: "" }),
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
    check_whole_node(node, root).issues
}

/// The properties behind the [`Reason::UnknownField`] issues
/// [`validate_node`] reports.
#[must_use]
pub fn unknown_keys_in_node(node: &Value, root: &str) -> Vec<UnknownKey> {
    check_whole_node(node, root).unknown
}

fn check_whole_node(node: &Value, root: &str) -> Found {
    match Node::parse_structure_rooted(node, root) {
        Err(e) => Found {
            issues: vec![e],
            unknown: Vec::new(),
        },
        Ok(parsed) => {
            let mut found = Found::default();
            check_node(
                Some(&parsed),
                node.as_object().map(|obj| Raw { obj, pointer: "" }),
                root,
                Tier::Save,
                &mut found,
            );
            found
        }
    }
}

fn report(found: &mut Found, tier: Tier, path: &str, reason: Reason) {
    if tier == Tier::Save || reason.fails_evaluation() {
        found.issues.push(ParseError::new(path, reason));
    }
}

/// `None` is a JSON-null composite slot. `raw` is the node's JSON object,
/// given only on the save tier.
fn check_node(
    node: Option<&Node>,
    raw: Option<Raw<'_>>,
    path: &str,
    tier: Tier,
    found: &mut Found,
) {
    let Some(node) = node else {
        return report(found, tier, path, Reason::ConditionMissing);
    };
    if let Some(raw) = raw {
        raw.report_unknown(is_node_property, tier, path, found);
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
    let entry = raw
        .filter(|_| type_str.eq_ignore_ascii_case(wire))
        .map(|o| match o.entry(wire) {
            Some((pointer, v)) => (v.as_object().unwrap_or(&empty), pointer),
            None => (&empty, String::new()),
        });
    let raw_payload = entry.as_ref().map(|(obj, pointer)| Raw { obj, pointer });
    check_payload(&payload, raw_payload, path, tier, found);
}

/// `type`, any kind's payload property (a node may carry payloads other than
/// the one its `type` names, which are parsed but not evaluated), or the web
/// rule editor's node key `_uid`, which rules it saved still carry.
fn is_node_property(name: &str) -> bool {
    name.eq_ignore_ascii_case("type") || name == "_uid" || ConditionKind::from_name(name).is_some()
}

/// `raw` is the child's JSON value and its pointer.
fn check_child(
    child: &Node,
    raw: Option<(String, &Value)>,
    parent: &str,
    index: usize,
    tier: Tier,
    found: &mut Found,
) {
    let raw = raw
        .as_ref()
        .and_then(|(pointer, v)| v.as_object().map(|obj| Raw { obj, pointer }));
    check_node(
        Some(child),
        raw,
        &node_child_path(parent, index, Some(child)),
        tier,
        found,
    );
}

/// `raw` is the payload's JSON object as written, given only on the save
/// tier and only when evaluation reads it.
fn check_payload(
    payload: &Payload,
    raw: Option<Raw<'_>>,
    path: &str,
    tier: Tier,
    found: &mut Found,
) {
    if let Some(raw) = raw {
        let fields = payload.fields();
        raw.report_unknown(
            |k| fields.iter().any(|f| f.eq_ignore_ascii_case(k)),
            tier,
            path,
            found,
        );
        for &field in required_fields(payload) {
            if raw.get(field).is_none_or(Value::is_null) {
                report(found, tier, path, Reason::FieldMissing(field));
            }
        }
    }
    let raw_entry = |name: &str| raw.and_then(|o| o.entry(name));
    // An operand `required_fields` already reports missing is not also
    // reported for the default it reads as.
    let written = |name: &str| raw.is_none_or(|o| o.get(name).is_some_and(|v| !v.is_null()));

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
            let raw_conditions = raw_entry("conditions")
                .and_then(|(pointer, v)| v.as_array().map(|items| (pointer, items)));
            for (i, child) in conditions.iter().enumerate() {
                let raw_child = raw_conditions.as_ref().and_then(|(pointer, items)| {
                    items.get(i).map(|v| (format!("{pointer}/{i}"), v))
                });
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
                check_child(c, raw_entry("child"), path, 0, tier, found);
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
                    check_child(c, raw_entry("child"), path, 0, tier, found);
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
fn negative(bound: i32, field: &'static str, tier: Tier, path: &str, found: &mut Found) {
    if bound < 0 {
        report(found, tier, path, Reason::MinutesNegative(field));
    }
}

fn undefined<E>(
    value: EnumValue<E>,
    field: &'static str,
    tier: Tier,
    path: &str,
    found: &mut Found,
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
    found: &mut Found,
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
