//! Leaf ids (engine-semantics.md §2.2): pre-order, depth-first,
//! left-to-right. Containers are unwrapped and never receive ids, but a
//! container missing its payload or children is a leaf. A stored rule's
//! kinds are its root kind and its leaves' kinds.

use serde_json::Value;

use crate::enums::WireEnum;
use crate::model::{ConditionKind, Node, parse_payload};

/// The leaves of `root` in pre-order; the vector index is the leaf id. A
/// `None` is a JSON-null composite slot, which evaluates false.
#[must_use]
pub(crate) fn collect_leaves(root: &Node) -> Vec<Option<&Node>> {
    let mut leaves = Vec::new();
    walk(Some(root), &mut leaves);
    leaves
}

fn walk<'a>(node: Option<&'a Node>, leaves: &mut Vec<Option<&'a Node>>) {
    match node.and_then(Node::container) {
        Some(container) => container.children().for_each(|child| walk(child, leaves)),
        None => leaves.push(node),
    }
}

/// Whether a stored rule's root kind, or the kind of any leaf of its tree,
/// satisfies `matches`, reading the rule as
/// [`references_wall_clock`](crate::wall_clock::references_wall_clock)
/// documents.
pub(crate) fn rule_references(
    condition_type: &str,
    condition_params: &Value,
    matches: impl Fn(ConditionKind) -> bool,
) -> bool {
    let Some(kind) = ConditionKind::from_name(condition_type) else {
        return false;
    };
    if matches(kind) {
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
            .is_some_and(&matches)
    })
}
