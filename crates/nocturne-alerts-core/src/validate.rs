//! Evaluability and save-time checks over parsed condition trees
//! (engine-semantics.md §1.4).
//!
//! Two tiers share one walk. Problems whose [`Reason::fails_evaluation`] is
//! true make evaluation fail, so parsing rejects them and the rule is
//! skipped. The rest leave a rule that evaluates but can never mean what its
//! author wrote (a node that is always false or always true); stored rules
//! keep evaluating them, and saving a rule rejects them.

use serde_json::Value;

use crate::enums::{EnumValue, Spelled};
use crate::model::{ConditionKind, Node, ParseError, Payload, Reason, parse_payload_structure};
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
    check_node(Some(node), root, Tier::Evaluation, &mut found);
    found.into_iter().next()
}

/// [`first_evaluation_fault`] for a rule body, rooted at its kind's wire name.
pub(crate) fn first_evaluation_fault_in_payload(payload: &Payload) -> Option<ParseError> {
    let mut found = Vec::new();
    check_payload(payload, payload.kind().wire(), Tier::Evaluation, &mut found);
    found.into_iter().next()
}

/// Every problem saving a rule body should reject: `condition_params` as
/// stored for `condition_type`. A structurally malformed payload reports only
/// its first error, as the reader stops there.
#[must_use]
pub fn validate_rule(condition_type: &str, condition_params: &Value) -> Vec<ParseError> {
    let Some(kind) =
        ConditionKind::from_wire(condition_type).filter(|k| k.wire() == condition_type)
    else {
        let reason = match ConditionKind::resolve(condition_type) {
            Some(_) => Reason::NonCanonicalType,
            None => Reason::UnknownKind,
        };
        return vec![ParseError::new(condition_type, reason)];
    };
    if condition_params.is_null() {
        return vec![ParseError::new(kind.wire(), Reason::PayloadMissing)];
    }
    match parse_payload_structure(kind, condition_params) {
        Err(e) => vec![e],
        Ok(payload) => {
            let mut found = Vec::new();
            check_payload(&payload, kind.wire(), Tier::Save, &mut found);
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
        Ok(node) => {
            let mut found = Vec::new();
            check_node(Some(&node), root, Tier::Save, &mut found);
            found
        }
    }
}

fn report(found: &mut Vec<ParseError>, tier: Tier, path: &str, reason: Reason) {
    if tier == Tier::Save || reason.fails_evaluation() {
        found.push(ParseError::new(path, reason));
    }
}

/// `None` is a JSON-null composite slot.
fn check_node(node: Option<&Node>, path: &str, tier: Tier, found: &mut Vec<ParseError>) {
    let Some(node) = node else {
        return report(found, tier, path, Reason::ConditionMissing);
    };
    let Some(type_str) = node.type_str.as_deref() else {
        return report(found, tier, path, Reason::TypeMissing);
    };
    let Some(payload) = node.dispatch() else {
        return report(found, tier, path, Reason::UnknownKind);
    };
    if type_str != payload.kind().wire() {
        report(found, tier, path, Reason::NonCanonicalType);
    }
    check_payload(&payload, path, tier, found);
}

fn check_child(child: &Node, parent: &str, index: usize, tier: Tier, found: &mut Vec<ParseError>) {
    check_node(
        Some(child),
        &node_child_path(parent, index, Some(child)),
        tier,
        found,
    );
}

fn check_payload(payload: &Payload, path: &str, tier: Tier, found: &mut Vec<ParseError>) {
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
            for (i, child) in conditions.iter().enumerate() {
                match child {
                    Some(c) => check_child(c, path, i, tier, found),
                    None => check_node(None, &node_child_path(path, i, None), tier, found),
                }
            }
            None
        }
        Payload::Not(p) => match &p.child {
            None => Some(Reason::ChildMissing),
            Some(c) => {
                check_child(c, path, 0, tier, found);
                None
            }
        },
        Payload::Sustained(p) => {
            if p.minutes <= 0 {
                report(found, tier, path, Reason::MinutesNotPositive);
            }
            match &p.child {
                None => Some(Reason::ChildMissing),
                Some(c) => {
                    check_child(c, path, 0, tier, found);
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
        Payload::LoopStale(p) | Payload::LoopEnactionStale(p) => operator_problem(&p.operator),
        Payload::Staleness(p) => operator_problem(&p.operator),
        Payload::Predicted(p) => operator_problem(&p.operator),
        Payload::TempBasal(p) => operator_problem(&p.operator),
        Payload::TrackerAge(p) => operator_problem(&p.operator),
        Payload::TimeSinceLastCarb(p) | Payload::TimeSinceLastBolus(p) => {
            matches!(p.operator, EnumValue::Undefined(_)).then_some(Reason::UnknownOperator)
        }
        Payload::SignalLoss(_)
        | Payload::Trend(_)
        | Payload::TimeOfDay(_)
        | Payload::PumpSuspended(_)
        | Payload::OverrideActive(_)
        | Payload::DoNotDisturb(_)
        | Payload::GlucoseBucket(_)
        | Payload::DayOfWeek(_)
        | Payload::PumpState(_)
        | Payload::StateSpanActive(_)
        | Payload::SleepSessionActive(_) => None,
    };
    if let Some(reason) = problem {
        report(found, tier, path, reason);
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
