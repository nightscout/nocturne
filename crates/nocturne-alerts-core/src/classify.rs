//! Scope classification for scoped Do Not Disturb (ADR 0004).
//!
//! A rule's *scope class* — `low` | `high` | `composite` | `undirected` — is
//! derived from the **directional leaves** of its condition tree, so a scoped
//! `lows`/`highs` mute can decide whether it silences the rule. This lives in the
//! crate (not a host) because each directional leaf encodes its direction in its
//! own field, and the crate already owns condition-tree parsing — re-encoding all
//! of that in C#/Kotlin is exactly the drift this avoids.
//!
//! Directional leaves and their low/high side:
//! - `threshold.direction`      `below` → low,       `above` → high
//! - `rate_of_change.direction` `falling` → low,     `rising` → high
//! - `predicted.operator`       `<` `<=` → low,      `>` `>=` → high  (`==` → none)
//! - `glucose_bucket.buckets`   very_low/low → low,  high/very_high → high
//!   (in_range/tight_range → neither; a set leaf can span both)
//!
//! `composite`/`sustained` recurse; everything else is non-directional. Any
//! directional leaf reached **under a `not`** taints the whole rule to
//! `undirected` (negation flips the clinical meaning — conservative, rare).

use crate::model::{ConditionKind, Node, Payload, parse_payload};
use serde_json::Value;

/// A rule's low/high classification. Wire strings match the server
/// `RuleScopeClass` and the device's `RuleScopeClass`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScopeClass {
    Low,
    High,
    Composite,
    Undirected,
}

impl ScopeClass {
    pub fn wire(self) -> &'static str {
        match self {
            ScopeClass::Low => "low",
            ScopeClass::High => "high",
            ScopeClass::Composite => "composite",
            ScopeClass::Undirected => "undirected",
        }
    }
}

/// Classifies a rule from its root `condition_type` + `condition_params`. Mirrors
/// the engine's root handling (`parse_payload` then walk), and silent-fails to
/// `Undirected` (all-only) on an unknown type or malformed params — the safe
/// default that never lets a scoped mute silence an unclassifiable rule.
pub fn classify(condition_type: &str, condition_params: &Value) -> ScopeClass {
    let mut acc = Acc::default();
    if let Some(kind) = ConditionKind::resolve(condition_type)
        && let Ok(payload) = parse_payload(kind, condition_params)
    {
        walk_kind(kind, Some(&payload), false, &mut acc);
    }
    acc.resolve()
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Side {
    Low,
    High,
}

#[derive(Default)]
struct Acc {
    low: bool,
    high: bool,
    /// A directional leaf was reached under a `not` — forces `undirected`.
    tainted_by_not: bool,
}

impl Acc {
    fn mark(&mut self, side: Side, under_not: bool) {
        if under_not {
            self.tainted_by_not = true;
        } else {
            match side {
                Side::Low => self.low = true,
                Side::High => self.high = true,
            }
        }
    }

    fn resolve(&self) -> ScopeClass {
        if self.tainted_by_not {
            return ScopeClass::Undirected;
        }
        match (self.low, self.high) {
            (true, false) => ScopeClass::Low,
            (false, true) => ScopeClass::High,
            (true, true) => ScopeClass::Composite,
            (false, false) => ScopeClass::Undirected,
        }
    }
}

/// Walks a child node, mirroring `eval_node`: resolve its kind, take the payload
/// only when the type matches the canonical wire name, recurse.
fn walk_node(node: Option<&Node>, under_not: bool, acc: &mut Acc) {
    let Some(node) = node else { return };
    let Some(type_str) = node.type_str.as_deref() else {
        return;
    };
    let Some(kind) = ConditionKind::resolve(type_str) else {
        return;
    };
    let lower = type_str.to_lowercase();
    let payload = if lower == kind.wire() {
        node.payload(&lower)
    } else {
        None
    };
    walk_kind(kind, payload, under_not, acc);
}

fn walk_kind(kind: ConditionKind, payload: Option<&Payload>, under_not: bool, acc: &mut Acc) {
    // No payload (absent, or an enum-name-spelled type that finds no snake_case
    // payload) contributes nothing — the engine's `default_payload` likewise has
    // no direction, so classify and the engine agree.
    let Some(payload) = payload else { return };
    match (kind, payload) {
        (ConditionKind::Threshold, Payload::Threshold(p)) => {
            if let Some(s) = threshold_side(p.direction.as_deref()) {
                acc.mark(s, under_not);
            }
        }
        (ConditionKind::RateOfChange, Payload::RateOfChange(p)) => {
            if let Some(s) = roc_side(p.direction.as_deref()) {
                acc.mark(s, under_not);
            }
        }
        (ConditionKind::Predicted, Payload::Predicted(p)) => {
            if let Some(s) = operator_side(p.operator.as_deref()) {
                acc.mark(s, under_not);
            }
        }
        (ConditionKind::GlucoseBucket, Payload::GlucoseBucket(p)) => {
            if let Some(buckets) = p.buckets.as_deref() {
                for &b in buckets {
                    if let Some(s) = bucket_side(b) {
                        acc.mark(s, under_not);
                    }
                }
            }
        }
        (ConditionKind::Composite, Payload::Composite(p)) => {
            if let Some(conditions) = &p.conditions {
                for child in conditions.iter().flatten() {
                    walk_node(Some(child), under_not, acc);
                }
            }
        }
        (ConditionKind::Not, Payload::Not(p)) => {
            walk_node(p.child.as_deref(), true, acc);
        }
        (ConditionKind::Sustained, Payload::Sustained(p)) => {
            walk_node(p.child.as_deref(), under_not, acc);
        }
        // Every other kind is non-directional and contributes nothing.
        _ => {}
    }
}

fn threshold_side(direction: Option<&str>) -> Option<Side> {
    match direction.map(str::to_lowercase).as_deref() {
        Some("below") => Some(Side::Low),
        Some("above") => Some(Side::High),
        _ => None,
    }
}

fn roc_side(direction: Option<&str>) -> Option<Side> {
    match direction.map(str::to_lowercase).as_deref() {
        Some("falling") => Some(Side::Low),
        Some("rising") => Some(Side::High),
        _ => None,
    }
}

fn operator_side(operator: Option<&str>) -> Option<Side> {
    // Exact-match on the comparison symbols, mirroring `compare.rs` — do NOT
    // lowercase/trim (these are symbols, not words; the engine matches exactly).
    match operator {
        Some("<") | Some("<=") => Some(Side::Low),
        Some(">") | Some(">=") => Some(Side::High),
        _ => None,
    }
}

/// `GlucoseBucket` ordinals (declaration order): 0 very_low, 1 low, 2 tight_range,
/// 3 in_range, 4 high, 5 very_high. In-range buckets are neither side.
fn bucket_side(ordinal: i64) -> Option<Side> {
    match ordinal {
        0 | 1 => Some(Side::Low),
        4 | 5 => Some(Side::High),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn class(condition_type: &str, params: Value) -> ScopeClass {
        classify(condition_type, &params)
    }

    fn threshold(direction: &str) -> Value {
        json!({ "type": "threshold", "threshold": { "direction": direction, "value": 70 } })
    }

    #[test]
    fn threshold_below_is_low() {
        assert_eq!(
            class("threshold", json!({"direction":"below","value":70})),
            ScopeClass::Low
        );
    }

    #[test]
    fn threshold_above_is_high() {
        assert_eq!(
            class("threshold", json!({"direction":"above","value":250})),
            ScopeClass::High
        );
    }

    #[test]
    fn rate_of_change_falling_is_low() {
        assert_eq!(
            class("rate_of_change", json!({"direction":"falling","rate":2})),
            ScopeClass::Low
        );
    }

    #[test]
    fn rate_of_change_rising_is_high() {
        assert_eq!(
            class("rate_of_change", json!({"direction":"rising","rate":2})),
            ScopeClass::High
        );
    }

    #[test]
    fn predicted_less_than_is_low() {
        assert_eq!(
            class(
                "predicted",
                json!({"operator":"<","value":70,"within_minutes":15})
            ),
            ScopeClass::Low
        );
    }

    #[test]
    fn predicted_greater_than_is_high() {
        assert_eq!(
            class(
                "predicted",
                json!({"operator":">=","value":250,"within_minutes":15})
            ),
            ScopeClass::High
        );
    }

    #[test]
    fn predicted_equals_is_undirected() {
        assert_eq!(
            class(
                "predicted",
                json!({"operator":"==","value":100,"within_minutes":15})
            ),
            ScopeClass::Undirected
        );
    }

    #[test]
    fn glucose_bucket_low_side_is_low() {
        assert_eq!(
            class("glucose_bucket", json!({"buckets":[0,1]})),
            ScopeClass::Low
        );
    }

    #[test]
    fn glucose_bucket_high_side_is_high() {
        assert_eq!(
            class("glucose_bucket", json!({"buckets":[4,5]})),
            ScopeClass::High
        );
    }

    #[test]
    fn glucose_bucket_spanning_both_is_composite() {
        assert_eq!(
            class("glucose_bucket", json!({"buckets":[0,5]})),
            ScopeClass::Composite
        );
    }

    #[test]
    fn glucose_bucket_in_range_only_is_undirected() {
        // 2 tight_range, 3 in_range — neither side.
        assert_eq!(
            class("glucose_bucket", json!({"buckets":[2,3]})),
            ScopeClass::Undirected
        );
    }

    #[test]
    fn signal_loss_is_undirected() {
        assert_eq!(
            class("signal_loss", json!({"timeout_minutes":20})),
            ScopeClass::Undirected
        );
    }

    #[test]
    fn unknown_type_is_undirected() {
        assert_eq!(class("teleport", json!({})), ScopeClass::Undirected);
    }

    #[test]
    fn composite_low_and_nondirectional_is_low() {
        // The common real shape: "low AND IOB-low AND dropping" is a LOW rule.
        let params = json!({
            "operator": "and",
            "conditions": [
                threshold("below"),
                { "type": "iob", "iob": { "operator": "<", "value": 1 } },
                { "type": "rate_of_change", "rate_of_change": { "direction": "falling", "rate": 1 } },
            ]
        });
        assert_eq!(class("composite", params), ScopeClass::Low);
    }

    #[test]
    fn composite_mixed_directions_is_composite() {
        let params = json!({
            "operator": "or",
            "conditions": [threshold("below"), threshold("above")]
        });
        assert_eq!(class("composite", params), ScopeClass::Composite);
    }

    #[test]
    fn not_directional_is_undirected() {
        let params = json!({ "child": threshold("above") });
        assert_eq!(class("not", params), ScopeClass::Undirected);
    }

    #[test]
    fn composite_with_a_negated_directional_leaf_is_undirected() {
        // A low leaf is present, but a directional leaf under NOT taints the whole rule.
        let params = json!({
            "operator": "and",
            "conditions": [threshold("below"), { "type": "not", "not": { "child": threshold("above") } }]
        });
        assert_eq!(class("composite", params), ScopeClass::Undirected);
    }

    #[test]
    fn sustained_passes_through_to_child_direction() {
        let params = json!({ "child": threshold("below") });
        assert_eq!(class("sustained", params), ScopeClass::Low);
    }

    #[test]
    fn composite_sustained_threshold_is_low() {
        // sustained nested inside a composite still passes its child's direction up.
        let params = json!({
            "operator": "and",
            "conditions": [{ "type": "sustained", "sustained": { "child": threshold("below") } }]
        });
        assert_eq!(class("composite", params), ScopeClass::Low);
    }

    #[test]
    fn double_not_directional_is_undirected() {
        // Double negation still taints — conservative, not clever.
        let params = json!({ "child": { "type": "not", "not": { "child": threshold("below") } } });
        assert_eq!(class("not", params), ScopeClass::Undirected);
    }
}
