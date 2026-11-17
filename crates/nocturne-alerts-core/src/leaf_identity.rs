//! Leaf ids (engine-semantics.md §2.2): pre-order, depth-first,
//! left-to-right. Containers are unwrapped and never receive ids.

use crate::model::Node;

/// The leaves of `root` in pre-order; the vector index is the leaf id.
#[must_use]
pub(crate) fn collect_leaves(root: &Node) -> Vec<&Node> {
    let mut leaves = Vec::new();
    walk(root, &mut leaves);
    leaves
}

fn walk<'a>(node: &'a Node, leaves: &mut Vec<&'a Node>) {
    match node.container() {
        Some(container) => container.children().for_each(|child| walk(child, leaves)),
        None => leaves.push(node),
    }
}
