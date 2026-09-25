//! Condition paths (engine-semantics.md §2.3).
//!
//! The root segment is the evaluation scope's root string as given: a rule
//! body's kind wire name, or a reserved root (`auto_resolve`, `snooze`). Each
//! descendant appends `[{index}].{type}` with the child's `type` as written,
//! casing preserved; a `not` or `sustained` child is always index `[0]`.

use crate::model::Node;

/// Reserved root for auto-resolve tree evaluation.
pub const AUTO_RESOLVE_ROOT: &str = "auto_resolve";

/// Reserved root for smart-snooze condition evaluation.
pub const SNOOZE_ROOT: &str = "snooze";

/// A missing `type` renders as an empty segment.
#[must_use]
pub fn child_path(parent: &str, index: usize, child_type: Option<&str>) -> String {
    format!("{parent}[{index}].{}", child_type.unwrap_or(""))
}

/// [`child_path`] for a parsed child; `None` is a JSON-null slot.
#[must_use]
pub fn node_child_path(parent: &str, index: usize, child: Option<&Node>) -> String {
    child_path(parent, index, child.and_then(|c| c.type_str.as_deref()))
}
