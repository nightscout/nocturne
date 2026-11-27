//! Save-time validation of condition trees (engine-semantics.md §1.4): a
//! save rejects every problem that keeps a tree from being evaluated.

use serde_json::Value;

use crate::model::{
    CURRENT_SCHEMA, ConditionKind, Node, ParseError, Reason, SCHEMA_VERSION, parse_payload,
};

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

/// The `composite{and}` a rule's snooze conditions evaluate as. Each stored
/// condition names its schema at its own root; the composite names schema 2
/// when every one does, and its children drop the name, which no node below a
/// root carries (engine-semantics.md §1.2). A condition that does not name
/// schema 2 leaves the composite naming none, so it is `unsupported_schema`.
#[must_use]
pub fn snooze_tree(conditions: &[Value]) -> Value {
    let current = conditions
        .iter()
        .all(|c| c.get(SCHEMA_VERSION).and_then(Value::as_u64) == Some(CURRENT_SCHEMA));
    let children: Vec<Value> = conditions
        .iter()
        .map(|c| match c {
            Value::Object(obj) => {
                let mut obj = obj.clone();
                obj.shift_remove(SCHEMA_VERSION);
                Value::Object(obj)
            }
            other => other.clone(),
        })
        .collect();
    let mut root = serde_json::Map::new();
    if current {
        root.insert(SCHEMA_VERSION.to_owned(), Value::from(CURRENT_SCHEMA));
    }
    root.insert("type".to_owned(), Value::from("composite"));
    root.insert(
        "composite".to_owned(),
        serde_json::json!({ "operator": "and", "conditions": children }),
    );
    Value::Object(root)
}
