//! Leaf ids (engine-semantics.md §2.2): pre-order, depth-first,
//! left-to-right. Containers are unwrapped and never receive ids, but a
//! container missing its payload or children is a leaf.

use crate::model::Node;

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
