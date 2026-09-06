//! `ConditionPath` grammar.
//!
//! The root segment is the evaluation scope's root string verbatim (the
//! canonical wire form of the rule's condition type for rule bodies, or the
//! reserved roots `auto_resolve` / `snooze`). Each descendant appends
//! `[{childIndex}].{child.Type}` where `child.Type` is the **verbatim** string
//! from the stored JSON — casing preserved. `not` and `sustained` children are
//! always index `[0]`.

/// Reserved root for auto-resolve tree evaluation.
pub const AUTO_RESOLVE_ROOT: &str = "auto_resolve";

/// Reserved root for smart-snooze condition evaluation.
pub const SNOOZE_ROOT: &str = "snooze";

/// `$"{parent}[{index}].{childType}"` — C# string interpolation renders a null
/// `Type` as the empty string.
pub fn child_path(parent: &str, index: usize, child_type: Option<&str>) -> String {
    format!("{parent}[{index}].{}", child_type.unwrap_or(""))
}
