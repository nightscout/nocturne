//! Scope classification for scoped Do Not Disturb (ADR 0004).
//!
//! A rule's scope class (`low`, `high`, `composite` or `undirected`) is
//! derived from the directional leaves of its condition tree, so a scoped
//! `lows`/`highs` mute can decide whether it silences the rule.
//!
//! Directional leaves and their low/high side:
//! - `threshold.direction`      `below` → low,       `above` → high
//! - `rate_of_change.direction` `falling` → low,     `rising` → high
//! - `predicted.operator`       `<` `<=` → low,      `>` `>=` → high  (`==` → none)
//! - `glucose_bucket.buckets`   very_low/low → low,  high/very_high → high
//!   (in_range/tight_range → neither; a set leaf can span both)
//!
//! Containers are unwrapped; everything else is non-directional. A
//! directional leaf under a `not` makes the whole rule `undirected`, since
//! negation flips its clinical meaning.

use serde_json::Value;

use crate::enums::{CmpOp, EnumValue, GlucoseBucket, RateDirection, ThresholdDirection};
use crate::model::{ConditionKind, Container, Node, Payload, parse_payload};

/// A rule's low/high classification.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum ScopeClass {
    Low,
    High,
    Composite,
    Undirected,
}

impl ScopeClass {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            ScopeClass::Low => "low",
            ScopeClass::High => "high",
            ScopeClass::Composite => "composite",
            ScopeClass::Undirected => "undirected",
        }
    }
}

/// Classifies a rule from its root `condition_type` + `condition_params`. An
/// unknown type or a body that cannot be evaluated is `Undirected`, so a
/// scoped mute never silences a rule it cannot classify.
#[must_use]
pub fn classify(condition_type: &str, condition_params: &Value) -> ScopeClass {
    let mut acc = Acc::default();
    if let Some(kind) = ConditionKind::resolve(condition_type)
        && let Ok(payload) = parse_payload(kind, condition_params)
    {
        walk(&Node::from_rule(kind, Some(payload)), false, &mut acc);
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
    under_not: bool,
}

impl Acc {
    fn mark(&mut self, side: Option<Side>, under_not: bool) {
        match side {
            None => {}
            Some(_) if under_not => self.under_not = true,
            Some(Side::Low) => self.low = true,
            Some(Side::High) => self.high = true,
        }
    }

    fn resolve(&self) -> ScopeClass {
        match (self.under_not, self.low, self.high) {
            (false, true, false) => ScopeClass::Low,
            (false, false, true) => ScopeClass::High,
            (false, true, true) => ScopeClass::Composite,
            _ => ScopeClass::Undirected,
        }
    }
}

fn walk(node: &Node, under_not: bool, acc: &mut Acc) {
    if let Some(container) = node.container() {
        let under_not = under_not || matches!(container, Container::Not(_));
        for child in container.children().flatten() {
            walk(child, under_not, acc);
        }
        return;
    }
    match node.dispatch().as_deref() {
        Some(Payload::Threshold(p)) => acc.mark(
            p.direction.value.map(|d| match d {
                ThresholdDirection::Below => Side::Low,
                ThresholdDirection::Above => Side::High,
            }),
            under_not,
        ),
        Some(Payload::RateOfChange(p)) => acc.mark(
            p.direction.value.map(|d| match d {
                RateDirection::Falling => Side::Low,
                RateDirection::Rising => Side::High,
            }),
            under_not,
        ),
        Some(Payload::Predicted(p)) => acc.mark(
            match p.operator.value {
                Some(CmpOp::Lt | CmpOp::Le) => Some(Side::Low),
                Some(CmpOp::Gt | CmpOp::Ge) => Some(Side::High),
                Some(CmpOp::Eq) | None => None,
            },
            under_not,
        ),
        Some(Payload::GlucoseBucket(p)) => {
            for bucket in p.buckets.iter().flatten() {
                acc.mark(bucket_side(*bucket), under_not);
            }
        }
        _ => {}
    }
}

fn bucket_side(bucket: EnumValue<GlucoseBucket>) -> Option<Side> {
    match bucket.known()? {
        GlucoseBucket::VeryLow | GlucoseBucket::Low => Some(Side::Low),
        GlucoseBucket::High | GlucoseBucket::VeryHigh => Some(Side::High),
        GlucoseBucket::TightRange | GlucoseBucket::InRange => None,
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
