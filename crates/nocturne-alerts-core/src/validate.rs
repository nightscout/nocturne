//! Evaluability and save-time checks over parsed condition trees
//! (engine-semantics.md §1.4).
//!
//! Two tiers share one walk. Problems whose [`Reason::fails_evaluation`] is
//! true make evaluation throw, so parsing rejects them and the rule is
//! skipped. The rest leave a rule that evaluates but can never mean what its
//! author wrote (a node that is always false or always true); stored rules
//! keep evaluating them, and saving a rule rejects them.

use serde_json::Value;

use crate::model::{
    ALERT_CMP_OP_NAMES, ConditionKind, Node, ParseError, Payload, Reason, default_payload,
    parse_payload_structure,
};
use crate::paths::child_path;

/// The operators `ComparisonOps.Compare` recognises (engine-semantics.md §3).
const COMPARE_OPERATORS: [&str; 5] = ["<", "<=", ">", ">=", "=="];

#[derive(Clone, Copy, PartialEq, Eq)]
enum Tier {
    Evaluation,
    Save,
}

/// The first problem, in pre-order, that makes evaluating the tree rooted at
/// `node` (whose path is `root`) fail.
pub fn first_evaluation_fault(node: &Node, root: &str) -> Option<ParseError> {
    let mut found = Vec::new();
    check_node(Some(node), root, Tier::Evaluation, &mut found);
    found.into_iter().next()
}

/// [`first_evaluation_fault`] for a rule body: `payload` evaluated as `kind`,
/// rooted at the kind's wire name.
pub fn first_evaluation_fault_in_payload(
    kind: ConditionKind,
    payload: &Payload,
) -> Option<ParseError> {
    let mut found = Vec::new();
    check_payload(payload, kind.wire(), Tier::Evaluation, &mut found);
    found.into_iter().next()
}

/// Every problem saving a rule body should reject: `condition_params` as
/// stored for `condition_type`. A structurally malformed payload reports only
/// its first error, as the reader stops there.
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
        report(found, tier, path, Reason::ConditionMissing);
        return;
    };
    let Some(type_str) = node.type_str.as_deref() else {
        report(found, tier, path, Reason::TypeMissing);
        return;
    };
    let Some((kind, stored)) = node.dispatch() else {
        report(found, tier, path, Reason::UnknownKind);
        return;
    };
    if type_str != kind.wire() {
        report(found, tier, path, Reason::NonCanonicalType);
    }
    let default;
    let payload = match stored {
        Some(p) => p,
        None => {
            default = default_payload(kind);
            &default
        }
    };
    check_payload(payload, path, tier, found);
}

fn check_child(child: &Node, parent: &str, index: usize, tier: Tier, found: &mut Vec<ParseError>) {
    let path = child_path(parent, index, child.type_str.as_deref());
    check_node(Some(child), &path, tier, found);
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
            match p.operator.as_deref() {
                None => report(found, tier, path, Reason::OperatorMissing),
                Some(op) if !is_any_ci(op, &["and", "or"]) => {
                    report(found, tier, path, Reason::UnknownOperator)
                }
                Some(_) => {}
            }
            for (i, child) in conditions.iter().enumerate() {
                match child {
                    Some(c) => check_child(c, path, i, tier, found),
                    None => check_node(None, &child_path(path, i, None), tier, found),
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
        Payload::Threshold(p) => direction_problem(p.direction.as_deref(), &["above", "below"]),
        Payload::RateOfChange(p) => {
            direction_problem(p.direction.as_deref(), &["rising", "falling"])
        }
        Payload::AlertState(p) => match p.state.as_deref() {
            None => Some(Reason::StateMissing),
            Some(s) if !is_any_ci(s, &["firing", "unacknowledged", "acknowledged"]) => {
                Some(Reason::UnknownState)
            }
            Some(_) => None,
        },
        Payload::Compare(p) => operator_problem(p.operator.as_deref()),
        Payload::MinutesCompare(p) => operator_problem(p.operator.as_deref()),
        Payload::Staleness(p) => operator_problem(p.operator.as_deref()),
        Payload::Predicted(p) => operator_problem(p.operator.as_deref()),
        Payload::TempBasal(p) => operator_problem(p.operator.as_deref()),
        Payload::TrackerAge(p) => operator_problem(p.operator.as_deref()),
        Payload::TimeSince(p) => (!(0..ALERT_CMP_OP_NAMES.len() as i64).contains(&p.operator))
            .then_some(Reason::UnknownOperator),
        Payload::SignalLoss(_)
        | Payload::Trend(_)
        | Payload::TimeOfDay(_)
        | Payload::ActiveFor(_)
        | Payload::GlucoseBucket(_)
        | Payload::DayOfWeek(_)
        | Payload::PumpState(_)
        | Payload::StateSpan(_)
        | Payload::SleepSession(_) => None,
    };
    if let Some(reason) = problem {
        report(found, tier, path, reason);
    }
}

/// The evaluators lowercase `direction` before matching it.
fn direction_problem(direction: Option<&str>, known: &[&str]) -> Option<Reason> {
    match direction {
        None => Some(Reason::DirectionMissing),
        Some(d) if !is_any_ci(d, known) => Some(Reason::UnknownDirection),
        Some(_) => None,
    }
}

/// Comparison operators match exactly; a missing one compares false.
fn operator_problem(operator: Option<&str>) -> Option<Reason> {
    (!operator.is_some_and(|op| COMPARE_OPERATORS.contains(&op))).then_some(Reason::UnknownOperator)
}

fn is_any_ci(s: &str, known: &[&str]) -> bool {
    known.contains(&s.to_lowercase().as_str())
}
