//! Save-time validation of condition trees (engine-semantics.md §1.4): a
//! save rejects every problem that keeps a tree from being evaluated.

use serde_json::Value;

use crate::model::{ConditionKind, Node, ParseError, Reason, parse_payload};

/// Every problem with a rule body, `condition_params` as stored for
/// `condition_type`, in pre-order; empty when it can be evaluated.
#[must_use]
pub fn validate_rule(condition_type: &str, condition_params: &Value) -> Vec<ParseError> {
    let Some(kind) = ConditionKind::from_wire(condition_type) else {
        return vec![ParseError::new(condition_type, Reason::UnknownKind)];
    };
    parse_payload(kind, condition_params)
        .err()
        .map(|e| e.0)
        .unwrap_or_default()
}

/// Every problem with a full condition node, with paths rooted at `root`
/// (`auto_resolve`, `snooze`, …).
#[must_use]
pub fn validate_node(node: &Value, root: &str) -> Vec<ParseError> {
    Node::parse_rooted(node, root)
        .err()
        .map(|e| e.0)
        .unwrap_or_default()
}
