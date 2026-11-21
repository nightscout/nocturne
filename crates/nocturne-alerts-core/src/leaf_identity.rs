//! Leaf ids (engine-semantics.md §2.2): pre-order, depth-first,
//! left-to-right. Containers are unwrapped and never receive ids.

use crate::model::Node;
use crate::paths::node_child_path;

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
