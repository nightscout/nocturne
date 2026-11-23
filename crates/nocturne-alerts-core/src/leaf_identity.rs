//! Leaf ids (engine-semantics.md §2.2): pre-order, depth-first,
//! left-to-right. Containers are unwrapped and never receive ids.

use crate::model::{Node, Payload};
use crate::paths::node_child_path;

/// The leaves of `root` in pre-order; the vector index is the leaf id.
#[must_use]
pub(crate) fn collect_leaves(root: &Node) -> Vec<&Node> {
    collect_leaves_with_polarity(root)
        .into_iter()
        .map(|(leaf, _)| leaf)
        .collect()
}

/// [`collect_leaves`], each with whether it is under an odd number of `not`s
/// (engine-semantics.md §3, clear margin).
#[must_use]
pub(crate) fn collect_leaves_with_polarity(root: &Node) -> Vec<(&Node, bool)> {
    let mut leaves = Vec::new();
    walk(root, false, &mut leaves);
    leaves
}

fn walk<'a>(node: &'a Node, negated: bool, leaves: &mut Vec<(&'a Node, bool)>) {
    let negated = negated ^ matches!(node.payload(), Payload::Not(_));
    match node.container() {
        Some(container) => container
            .children()
            .for_each(|child| walk(child, negated, leaves)),
        None => leaves.push((node, negated)),
    }
}

/// Every node of `root` in pre-order with its condition path (§2.3), `root`
/// at `root_path`. Leaves come in leaf-id order.
#[must_use]
pub fn nodes_with_paths(root: &Node, root_path: String) -> Vec<(&Node, String)> {
    let mut nodes = Vec::new();
    walk_paths(root, root_path, &mut nodes);
    nodes
}

fn walk_paths<'a>(node: &'a Node, path: String, nodes: &mut Vec<(&'a Node, String)>) {
    let children = node.container().map(|container| {
        container
            .children()
            .enumerate()
            .map(|(i, child)| (child, node_child_path(&path, i, child)))
            .collect::<Vec<_>>()
    });
    nodes.push((node, path));
    for (child, child_path) in children.into_iter().flatten() {
        walk_paths(child, child_path, nodes);
    }
}
