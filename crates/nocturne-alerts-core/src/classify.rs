//! Scope classification for scoped Do Not Disturb (ADR 0004).
//!
//! A rule's scope class (`low`, `high`, `composite` or `undirected`) is
//! derived from the directional leaves of its condition tree, so a scoped
//! `lows`/`highs` mute can decide whether it silences the rule.
//!
//! Directional leaves and their low/high side:
//! - `threshold`, `rate_of_change` and `predicted` by their `operator`:
//!   `<` `<=` → low, `>` `>=` → high (`==` → none)
//! - `glucose_bucket.buckets`   very_low/low → low,  high/very_high → high
//!   (in_range/tight_range → neither; a set leaf can span both)
//! - `glucose_extreme` by its `operator` where it agrees with `of`: `min` with
//!   `<` `<=` → low, `max` with `>` `>=` → high; every other pairing → none
//! - `glucose_count` by the `operator` of its `reading`, as a `threshold`'s
//!
//! Containers are unwrapped; everything else is non-directional. A
//! directional leaf under a `not` makes the whole rule `undirected`, since
//! negation flips its clinical meaning.

use serde_json::Value;

use crate::enums::{CmpOp, GlucoseBucket, WindowExtreme};
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
    if let Some(kind) = ConditionKind::from_wire(condition_type)
        && let Ok(payload) = parse_payload(kind, condition_params)
    {
        walk(&Node::from(payload), false, &mut acc);
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
        for child in container.children() {
            walk(child, under_not, acc);
        }
        return;
    }
    match node.payload() {
        Payload::Threshold(p) => acc.mark(operator_side(p.operator), under_not),
        Payload::RateOfChange(p) => acc.mark(operator_side(p.operator), under_not),
        Payload::Predicted(p) => acc.mark(operator_side(p.operator), under_not),
        Payload::GlucoseBucket(p) => {
            for &bucket in &p.buckets {
                acc.mark(bucket_side(bucket), under_not);
            }
        }
        Payload::GlucoseExtreme(p) => acc.mark(extreme_side(p.of, p.operator), under_not),
        Payload::GlucoseCount(p) => acc.mark(operator_side(p.reading.operator), under_not),
        // `glucose_change` is left undirected: a fast fall is not a low, and a mute that silenced
        // it would fail unsafe.
        _ => {}
    }
}

/// A window's lowest reading is a low only when it is compared downward, and its highest a high
/// only when compared upward: `min > 70` and `max < 250` say the window stayed in range.
fn extreme_side(of: WindowExtreme, operator: CmpOp) -> Option<Side> {
    match (of, operator_side(operator)) {
        (WindowExtreme::Min, Some(Side::Low)) => Some(Side::Low),
        (WindowExtreme::Max, Some(Side::High)) => Some(Side::High),
        _ => None,
    }
}

fn operator_side(operator: CmpOp) -> Option<Side> {
    match operator {
        CmpOp::Lt | CmpOp::Le => Some(Side::Low),
        CmpOp::Gt | CmpOp::Ge => Some(Side::High),
        CmpOp::Eq => None,
    }
}

fn bucket_side(bucket: GlucoseBucket) -> Option<Side> {
    match bucket {
        GlucoseBucket::VeryLow | GlucoseBucket::Low => Some(Side::Low),
        GlucoseBucket::High | GlucoseBucket::VeryHigh => Some(Side::High),
        GlucoseBucket::TightRange | GlucoseBucket::InRange => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// `params` as a stored rule body, which names its schema.
    fn class(condition_type: &str, mut params: Value) -> ScopeClass {
        params["schema_version"] = json!(2);
        classify(condition_type, &params)
    }

    fn threshold(operator: &str) -> Value {
        json!({ "type": "threshold", "threshold": { "operator": operator, "value": 70 } })
    }

    fn not(child: Value) -> Value {
        json!({ "type": "not", "not": { "child": child } })
    }

    const SIDES: [(&str, ScopeClass); 5] = [
        ("<", ScopeClass::Low),
        ("<=", ScopeClass::Low),
        (">", ScopeClass::High),
        (">=", ScopeClass::High),
        ("==", ScopeClass::Undirected),
    ];

    #[test]
    fn a_threshold_takes_its_side_from_its_operator() {
        for (operator, side) in SIDES {
            assert_eq!(
                class("threshold", json!({ "operator": operator, "value": 70 })),
                side,
                "{operator}"
            );
        }
    }

    #[test]
    fn a_rate_of_change_takes_its_side_from_its_operator_whatever_its_sign() {
        for (operator, side) in SIDES {
            for value in [-2, 0, 2] {
                assert_eq!(
                    class(
                        "rate_of_change",
                        json!({ "operator": operator, "value": value })
                    ),
                    side,
                    "{operator} {value}"
                );
            }
        }
    }

    #[test]
    fn a_predicted_takes_its_side_from_its_operator() {
        for (operator, side) in SIDES {
            assert_eq!(
                class(
                    "predicted",
                    json!({ "operator": operator, "value": 70, "within_minutes": 15 })
                ),
                side,
                "{operator}"
            );
        }
    }

    #[test]
    fn a_directed_operator_under_not_is_undirected_and_an_equality_taints_nothing() {
        for (operator, side) in SIDES {
            let negated = class("not", json!({ "child": threshold(operator) }));
            assert_eq!(negated, ScopeClass::Undirected, "not {operator}");
            let beside_a_low = class(
                "composite",
                json!({ "operator": "and", "conditions": [threshold("<"), not(threshold(operator))] }),
            );
            let expected = if side == ScopeClass::Undirected {
                ScopeClass::Low
            } else {
                ScopeClass::Undirected
            };
            assert_eq!(beside_a_low, expected, "low and not {operator}");
        }
    }

    fn extreme(of: &str, operator: &str) -> Value {
        json!({ "of": of, "operator": operator, "value": 70, "window_minutes": 30 })
    }

    fn extreme_leaf(of: &str, operator: &str) -> Value {
        json!({ "type": "glucose_extreme", "glucose_extreme": extreme(of, operator) })
    }

    fn count(operator: &str) -> Value {
        json!({
            "reading": { "operator": operator, "value": 70 },
            "window_minutes": 30,
            "count": { "operator": ">=", "value": 3 }
        })
    }

    #[test]
    fn a_glucose_extreme_is_directed_only_where_its_end_and_operator_point_the_same_way() {
        let cases = [
            ("min", "<", ScopeClass::Low),
            ("min", "<=", ScopeClass::Low),
            ("max", ">", ScopeClass::High),
            ("max", ">=", ScopeClass::High),
            ("min", ">", ScopeClass::Undirected),
            ("min", ">=", ScopeClass::Undirected),
            ("max", "<", ScopeClass::Undirected),
            ("max", "<=", ScopeClass::Undirected),
            ("min", "==", ScopeClass::Undirected),
            ("max", "==", ScopeClass::Undirected),
        ];
        for (of, operator, side) in cases {
            assert_eq!(
                class("glucose_extreme", extreme(of, operator)),
                side,
                "{of} {operator}"
            );
        }
    }

    #[test]
    fn a_glucose_count_takes_its_side_from_its_reading_whatever_it_tallies() {
        for (operator, side) in SIDES {
            assert_eq!(class("glucose_count", count(operator)), side, "{operator}");
            let by_percent = json!({
                "reading": { "operator": operator, "value": 70 },
                "window_minutes": 30,
                "percent": { "operator": ">=", "value": 50 }
            });
            assert_eq!(
                class("glucose_count", by_percent),
                side,
                "percent of {operator}"
            );
        }
    }

    #[test]
    fn a_glucose_change_is_undirected_whatever_its_sign_and_operator() {
        for (operator, _) in SIDES {
            for value in [-30, 0, 30] {
                let params = json!({ "operator": operator, "value": value, "window_minutes": 15 });
                assert_eq!(
                    class("glucose_change", params),
                    ScopeClass::Undirected,
                    "{operator} {value}"
                );
            }
        }
    }

    #[test]
    fn a_directed_windowed_leaf_under_not_is_undirected() {
        assert_eq!(
            class("not", json!({ "child": extreme_leaf("min", "<") })),
            ScopeClass::Undirected
        );
        let negated_count = json!({ "type": "glucose_count", "glucose_count": count(">") });
        assert_eq!(
            class("not", json!({ "child": negated_count })),
            ScopeClass::Undirected
        );
    }

    #[test]
    fn a_windowed_leaf_keeps_the_side_of_a_threshold_beside_it_and_makes_a_composite_of_the_other()
    {
        let same = json!({
            "operator": "and",
            "conditions": [threshold("<"), extreme_leaf("min", "<")]
        });
        assert_eq!(class("composite", same), ScopeClass::Low);
        let opposite = json!({
            "operator": "or",
            "conditions": [threshold("<"), extreme_leaf("max", ">")]
        });
        assert_eq!(class("composite", opposite), ScopeClass::Composite);
    }

    #[test]
    fn a_body_that_names_no_schema_is_undirected() {
        assert_eq!(
            classify("threshold", &json!({ "operator": "<", "value": 70 })),
            ScopeClass::Undirected
        );
    }

    #[test]
    fn glucose_bucket_low_side_is_low() {
        assert_eq!(
            class("glucose_bucket", json!({"buckets":["very_low","low"]})),
            ScopeClass::Low
        );
    }

    #[test]
    fn glucose_bucket_high_side_is_high() {
        assert_eq!(
            class("glucose_bucket", json!({"buckets":["high","very_high"]})),
            ScopeClass::High
        );
    }

    #[test]
    fn glucose_bucket_spanning_both_is_composite() {
        assert_eq!(
            class(
                "glucose_bucket",
                json!({"buckets":["very_low","very_high"]})
            ),
            ScopeClass::Composite
        );
    }

    #[test]
    fn glucose_bucket_in_range_only_is_undirected() {
        assert_eq!(
            class(
                "glucose_bucket",
                json!({"buckets":["tight_range","in_range"]})
            ),
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
                threshold("<"),
                { "type": "iob", "iob": { "operator": "<", "value": 1 } },
                { "type": "rate_of_change", "rate_of_change": { "operator": "<=", "value": -1 } },
            ]
        });
        assert_eq!(class("composite", params), ScopeClass::Low);
    }

    #[test]
    fn composite_mixed_directions_is_composite() {
        let params = json!({
            "operator": "or",
            "conditions": [threshold("<"), threshold(">")]
        });
        assert_eq!(class("composite", params), ScopeClass::Composite);
    }

    #[test]
    fn not_directional_is_undirected() {
        let params = json!({ "child": threshold(">") });
        assert_eq!(class("not", params), ScopeClass::Undirected);
    }

    #[test]
    fn composite_with_a_negated_directional_leaf_is_undirected() {
        // A low leaf is present, but a directional leaf under NOT taints the whole rule.
        let params = json!({
            "operator": "and",
            "conditions": [threshold("<"), not(threshold(">"))]
        });
        assert_eq!(class("composite", params), ScopeClass::Undirected);
    }

    #[test]
    fn sustained_passes_through_to_child_direction() {
        let params = json!({ "minutes": 15, "child": threshold("<") });
        assert_eq!(class("sustained", params), ScopeClass::Low);
    }

    #[test]
    fn composite_sustained_threshold_is_low() {
        // sustained nested inside a composite still passes its child's direction up.
        let params = json!({
            "operator": "and",
            "conditions": [{ "type": "sustained", "sustained": { "minutes": 15, "child": threshold("<") } }]
        });
        assert_eq!(class("composite", params), ScopeClass::Low);
    }

    #[test]
    fn double_not_directional_is_undirected() {
        // Double negation still taints — conservative, not clever.
        let params = json!({ "child": not(threshold("<")) });
        assert_eq!(class("not", params), ScopeClass::Undirected);
    }
}
