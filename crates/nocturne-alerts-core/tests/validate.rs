//! Evaluability (parse-time) and save-time validation of condition trees.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use chrono::{DateTime, TimeZone, Utc};
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Rule, evaluate_rule};
use nocturne_alerts_core::model::{ConditionKind, Node, ParseError, Reason, parse_payload};
use nocturne_alerts_core::validate::{validate_node, validate_rule};

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 12, 0, 0).unwrap()
}

fn low() -> Value {
    json!({ "type": "threshold", "threshold": { "direction": "below", "value": 70 } })
}

fn composite(operator: &str, conditions: Value) -> Value {
    json!({ "type": "composite", "composite": { "operator": operator, "conditions": conditions } })
}

fn parse_error(v: &Value) -> ParseError {
    Node::parse(v).expect_err("tree cannot be evaluated")
}

fn issue(path: &str, reason: Reason) -> ParseError {
    ParseError::new(path, reason)
}

// ---------------------------------------------------------------------------
// Shapes whose evaluation fails
// ---------------------------------------------------------------------------

#[test]
fn composite_without_a_conditions_list_cannot_be_evaluated() {
    let nested = composite(
        "and",
        json!([{ "type": "composite", "composite": { "operator": "or" } }]),
    );
    assert_eq!(
        parse_error(&nested),
        issue("composite[0].composite", Reason::ConditionsMissing)
    );
    let bare = composite("and", json!([{ "type": "composite" }]));
    assert_eq!(
        parse_error(&bare),
        issue("composite[0].composite", Reason::ConditionsMissing)
    );
}

#[test]
fn composite_with_conditions_but_no_operator_cannot_be_evaluated() {
    let tree = json!({ "type": "composite", "composite": { "conditions": [low()] } });
    assert_eq!(
        parse_error(&tree),
        issue("composite", Reason::OperatorMissing)
    );
}

#[test]
fn composite_without_operator_or_conditions_evaluates_false() {
    let tree = json!({ "type": "composite", "composite": { "conditions": [] } });
    assert!(Node::parse(&tree).is_ok());
}

#[test]
fn null_composite_slot_cannot_be_evaluated() {
    let tree = composite("and", json!([low(), null]));
    assert_eq!(
        parse_error(&tree),
        issue("composite[1].", Reason::ConditionMissing)
    );
}

#[test]
fn child_without_a_type_cannot_be_evaluated() {
    let not =
        json!({ "type": "not", "not": { "child": { "threshold": { "direction": "below" } } } });
    assert_eq!(parse_error(&not), issue("not[0].", Reason::TypeMissing));
    let slot = composite("or", json!([{ "type": null }]));
    assert_eq!(
        parse_error(&slot),
        issue("composite[0].", Reason::TypeMissing)
    );
    let root = json!({ "threshold": { "direction": "below", "value": 70 } });
    assert_eq!(
        Node::parse_rooted(&root, "auto_resolve").unwrap_err(),
        issue("auto_resolve", Reason::TypeMissing)
    );
}

#[test]
fn threshold_and_rate_of_change_without_a_direction_cannot_be_evaluated() {
    for (tree, path) in [
        (
            composite(
                "and",
                json!([{ "type": "threshold", "threshold": { "value": 70 } }]),
            ),
            "composite[0].threshold",
        ),
        (
            composite("and", json!([{ "type": "threshold" }])),
            "composite[0].threshold",
        ),
        (
            json!({ "type": "not", "not": { "child": { "type": "rate_of_change", "rate_of_change": { "rate": 3 } } } }),
            "not[0].rate_of_change",
        ),
    ] {
        assert_eq!(parse_error(&tree), issue(path, Reason::DirectionMissing));
    }
    assert_eq!(
        parse_payload(ConditionKind::Threshold, &json!({ "value": 70 })).unwrap_err(),
        issue("threshold", Reason::DirectionMissing)
    );
}

#[test]
fn a_multi_word_kind_spelled_by_its_member_name_drops_its_payload() {
    // "RateOfChange" resolves the kind but not the payload key, so the
    // evaluator sees the default payload, whose direction is missing.
    let tree = composite(
        "and",
        json!([{ "type": "RateOfChange", "rate_of_change": { "direction": "falling", "rate": 3 } }]),
    );
    assert_eq!(
        parse_error(&tree),
        issue("composite[0].RateOfChange", Reason::DirectionMissing)
    );
}

#[test]
fn alert_state_without_a_state_cannot_be_evaluated() {
    let payload = json!({ "alert_id": "00000000-0000-0000-0000-0000000000aa" });
    assert_eq!(
        parse_payload(ConditionKind::AlertState, &payload).unwrap_err(),
        issue("alert_state", Reason::StateMissing)
    );
}

#[test]
fn a_fault_the_evaluation_order_would_never_reach_still_rejects_the_tree() {
    let short_circuited = composite("or", json!([low(), null]));
    assert!(Node::parse(&short_circuited).is_err());
    let behind_zero_minutes = json!({
        "type": "sustained",
        "sustained": { "minutes": 0, "child": { "type": "threshold" } }
    });
    assert_eq!(
        parse_error(&behind_zero_minutes),
        issue("sustained[0].threshold", Reason::DirectionMissing)
    );
}

#[test]
fn problems_that_only_evaluate_false_or_true_still_parse() {
    for tree in [
        json!({ "type": "warp_drive" }),
        json!({ "type": "not", "not": { "child": { "type": "warp_drive" } } }),
        json!({ "type": "not", "not": {} }),
        json!({ "type": "sustained", "sustained": { "minutes": 0, "child": low() } }),
        composite("xor", json!([low()])),
        composite("and", json!([])),
        json!({ "type": "threshold", "threshold": { "direction": "sideways", "value": 70 } }),
        json!({ "type": "iob", "iob": { "operator": "=>", "value": 1 } }),
        json!({ "type": "PumpState", "pump_state": { "mode": "Suspended", "is_active": true } }),
    ] {
        assert!(Node::parse(&tree).is_ok(), "{tree}");
    }
}

// ---------------------------------------------------------------------------
// Driver behaviour
// ---------------------------------------------------------------------------

fn rule(condition_params: Value) -> Rule {
    Rule {
        id: Uuid::from_u128(1),
        condition_type: ConditionKind::Composite,
        condition_params,
        confirmation_readings: 1,
        hysteresis_minutes: 0,
        auto_resolve_enabled: false,
        auto_resolve_params: None,
    }
}

fn reading(value: i64) -> SensorContext {
    serde_json::from_value(json!({
        "latest_value": value,
        "latest_timestamp": now(),
        "last_reading_at": now(),
    }))
    .unwrap()
}

#[test]
fn a_rule_that_cannot_be_evaluated_is_skipped_with_its_state_untouched() {
    let mut state = EngineState::new();
    let good = rule(json!({ "operator": "and", "conditions": [low()] }));
    let opened = evaluate_rule(&good, &reading(60), now(), &mut state, true);
    assert!(opened.evaluation.is_some_and(|e| e.tracker.is_some()));
    let before = format!("{:?}", state.tracker.state(good.id));

    let bad = rule(json!({ "operator": "and", "conditions": [low(), null] }));
    let outcome = evaluate_rule(&bad, &reading(200), now(), &mut state, true);
    assert!(outcome.evaluation.is_none());
    assert_eq!(format!("{:?}", state.tracker.state(bad.id)), before);
}

#[test]
fn an_auto_resolve_tree_that_cannot_be_evaluated_never_resolves() {
    let mut state = EngineState::new();
    let mut r = rule(json!({ "operator": "and", "conditions": [low()] }));
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(composite(
        "or",
        json!([
            { "type": "threshold", "threshold": { "direction": "above", "value": 50 } },
            null
        ]),
    ));
    let outcome = evaluate_rule(&r, &reading(60), now(), &mut state, true);
    assert!(outcome.evaluation.is_some_and(|e| !e.auto_resolved));
    assert!(state.tracker.active_excursion_id(r.id).is_some());
}

// ---------------------------------------------------------------------------
// Save-time validation
// ---------------------------------------------------------------------------

#[test]
fn a_valid_rule_has_no_issues() {
    let params = json!({ "operator": "and", "conditions": [
        low(),
        { "type": "sustained", "sustained": { "minutes": 15, "child":
            { "type": "rate_of_change", "rate_of_change": { "direction": "falling", "rate": 2 } } } },
        { "type": "not", "not": { "child": { "type": "do_not_disturb", "do_not_disturb": { "is_active": true } } } },
        { "type": "time_since_last_carb", "time_since_last_carb": { "operator": ">=", "minutes": 30 } },
        { "type": "alert_state", "alert_state": { "alert_id": "00000000-0000-0000-0000-0000000000aa", "state": "Firing" } }
    ]});
    assert_eq!(validate_rule("composite", &params), vec![]);
}

#[test]
fn saving_reports_every_problem_in_the_tree() {
    let params = json!({ "operator": "AND", "conditions": [
        { "type": "warp_drive" },
        { "type": "Threshold", "threshold": { "direction": "below", "value": 70 } },
        { "type": "threshold", "threshold": { "direction": "sideways", "value": 70 } },
        { "type": "iob", "iob": { "operator": "=>", "value": 1 } },
        { "type": "time_since_last_bolus", "time_since_last_bolus": { "operator": 9, "minutes": 5 } },
        { "type": "alert_state", "alert_state": { "alert_id": "00000000-0000-0000-0000-0000000000aa", "state": "snoozed" } },
        { "type": "composite", "composite": { "operator": "xor", "conditions": [low()] } },
        { "type": "composite", "composite": { "operator": "or", "conditions": [] } },
        { "type": "not", "not": {} },
        { "type": "sustained", "sustained": { "minutes": 0 } },
        null,
        { "type": "rate_of_change" }
    ]});
    assert_eq!(
        validate_rule("composite", &params),
        vec![
            issue("composite[0].warp_drive", Reason::UnknownKind),
            issue("composite[1].Threshold", Reason::NonCanonicalType),
            issue("composite[2].threshold", Reason::UnknownDirection),
            issue("composite[3].iob", Reason::UnknownOperator),
            issue(
                "composite[4].time_since_last_bolus",
                Reason::UnknownOperator
            ),
            issue("composite[5].alert_state", Reason::UnknownState),
            issue("composite[6].composite", Reason::UnknownOperator),
            issue("composite[7].composite", Reason::ConditionsEmpty),
            issue("composite[8].not", Reason::ChildMissing),
            issue("composite[9].sustained", Reason::MinutesNotPositive),
            issue("composite[9].sustained", Reason::ChildMissing),
            issue("composite[10].", Reason::ConditionMissing),
            issue("composite[11].rate_of_change", Reason::DirectionMissing),
        ]
    );
}

#[test]
fn saving_rejects_a_root_that_is_not_the_wire_name_or_has_no_payload() {
    assert_eq!(
        validate_rule("Threshold", &json!({ "direction": "below", "value": 70 })),
        vec![issue("Threshold", Reason::NonCanonicalType)]
    );
    assert_eq!(
        validate_rule("warp_drive", &json!({})),
        vec![issue("warp_drive", Reason::UnknownKind)]
    );
    assert_eq!(
        validate_rule("threshold", &Value::Null),
        vec![issue("threshold", Reason::PayloadMissing)]
    );
    assert_eq!(
        validate_rule("threshold", &json!({})),
        vec![issue("threshold", Reason::DirectionMissing)]
    );
}

#[test]
fn saving_reports_the_first_structural_error_with_its_path_and_field() {
    let params = json!({ "operator": "and", "conditions": [
        low(),
        { "type": "not", "not": { "child": { "type": "iob", "iob": { "operator": "<", "value": "lots" } } } }
    ]});
    let issues = validate_rule("composite", &params);
    assert_eq!(
        issues,
        vec![issue(
            "composite[1].not[0].iob",
            Reason::InvalidField("value")
        )]
    );
    assert_eq!(issues[0].reason.field(), Some("value"));
}

#[test]
fn validating_a_node_roots_paths_at_the_scope() {
    let tree = composite(
        "and",
        json!([{ "type": "trend", "trend": { "bucket": "falling" } }, { "type": "cob" }]),
    );
    assert_eq!(
        validate_node(&tree, "auto_resolve"),
        vec![issue("auto_resolve[1].cob", Reason::UnknownOperator)]
    );
    assert_eq!(
        validate_node(&json!([]), "snooze"),
        vec![issue("snooze", Reason::NotAnObject)]
    );
}

#[test]
fn reasons_never_carry_payload_values() {
    let params = json!({ "operator": "and", "conditions": [
        { "type": "threshold", "threshold": { "direction": "sideways-123", "value": 70 } }
    ]});
    let rendered: Vec<String> = validate_rule("composite", &params)
        .iter()
        .map(ToString::to_string)
        .collect();
    assert_eq!(
        rendered,
        vec!["unknown_direction at 'composite[0].threshold' (field 'direction')"]
    );
}
