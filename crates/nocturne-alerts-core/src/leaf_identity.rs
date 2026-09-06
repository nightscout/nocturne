//! `LeafIdentity.AssignLeafIds`: pre-order, depth-first, left-to-right leaf id
//! assignment. Containers (composite/not/sustained) are unwrapped and never
//! receive ids — but a container whose payload (or required child/conditions
//! list) is missing fails the `when`-guard and IS a leaf. **[anomaly — normative]**

use crate::model::{Node, Payload};

/// Returns the leaves of `root` in pre-order; the vector index is the leaf id.
/// An element is `None` for a JSON-null child slot (C# would NRE walking it;
/// it force-evaluates to false).
pub fn collect_leaves(root: &Node) -> Vec<Option<&Node>> {
    let mut leaves = Vec::new();
    walk(root, &mut leaves);
    leaves
}

fn walk<'a>(node: &'a Node, leaves: &mut Vec<Option<&'a Node>>) {
    let lower = node.type_str.as_deref().map(str::to_lowercase);
    match lower.as_deref() {
        Some("composite") => {
            if let Some(Payload::Composite(p)) = node.payload("composite")
                && let Some(children) = &p.conditions
            {
                for child in children {
                    match child {
                        Some(c) => walk(c, leaves),
                        None => leaves.push(None),
                    }
                }
                return;
            }
        }
        Some("not") => {
            if let Some(Payload::Not(p)) = node.payload("not")
                && let Some(child) = &p.child
            {
                walk(child, leaves);
                return;
            }
        }
        Some("sustained") => {
            if let Some(Payload::Sustained(p)) = node.payload("sustained")
                && let Some(child) = &p.child
            {
                walk(child, leaves);
                return;
            }
        }
        _ => {}
    }
    leaves.push(Some(node));
}
