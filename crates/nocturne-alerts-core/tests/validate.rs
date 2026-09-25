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
use nocturne_alerts_core::validate::{
    UnknownKey, unknown_keys_in_node, unknown_keys_in_rule, validate_node, validate_rule,
};

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
            issue(
                "composite[9].sustained",
                Reason::MinutesNotPositive("minutes")
            ),
            issue("composite[9].sustained", Reason::ChildMissing),
            issue("composite[10].", Reason::ConditionMissing),
            issue("composite[11].rate_of_change", Reason::FieldMissing("rate")),
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
        vec![
            issue("threshold", Reason::FieldMissing("value")),
            issue("threshold", Reason::DirectionMissing)
        ]
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
        vec![
            issue("auto_resolve[1].cob", Reason::FieldMissing("value")),
            issue("auto_resolve[1].cob", Reason::UnknownOperator)
        ]
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

fn leaf(kind: &str, payload: Value) -> Value {
    json!({ "type": kind, kind: payload })
}

fn issues_of(kind: &str, payload: Value) -> Vec<ParseError> {
    validate_rule(
        "composite",
        &json!({ "operator": "and", "conditions": [leaf(kind, payload)] }),
    )
}

fn at_leaf(kind: &str, reasons: &[Reason]) -> Vec<ParseError> {
    reasons
        .iter()
        .map(|&r| issue(&format!("composite[0].{kind}"), r))
        .collect()
}

#[test]
fn saving_rejects_properties_no_node_or_payload_has() {
    assert_eq!(
        validate_rule("signal_loss", &json!({ "timeoutMinutes": 20 })),
        vec![
            issue("signal_loss", Reason::UnknownField),
            issue("signal_loss", Reason::FieldMissing("timeout_minutes")),
        ]
    );
    assert_eq!(
        issues_of(
            "threshold",
            json!({ "direction": "below", "threshold": 70 })
        ),
        at_leaf(
            "threshold",
            &[Reason::UnknownField, Reason::FieldMissing("value")]
        )
    );
    assert_eq!(
        issues_of(
            "pump_suspended",
            json!({ "is_active": true, "forMinutes": 10 })
        ),
        at_leaf("pump_suspended", &[Reason::UnknownField])
    );
    let stray = json!({ "operator": "and", "conditions": [
        { "type": "threshold", "threshold": { "direction": "below", "value": 70 }, "note": "x" }
    ]});
    assert_eq!(
        validate_rule("composite", &stray),
        vec![issue("composite[0].threshold", Reason::UnknownField)]
    );
}

fn unknown(path: &str, pointer: &str, key: &str) -> UnknownKey {
    UnknownKey {
        path: path.to_owned(),
        pointer: pointer.to_owned(),
        key: key.to_owned(),
    }
}

#[test]
fn unknown_keys_name_each_property_and_the_object_holding_it() {
    let params = json!({ "operator": "and", "conditions": [
        { "type": "threshold", "note": "x",
          "Threshold": { "direction": "below", "value": 70, "for_minutes": 5 } },
        { "type": "not", "not": { "child":
            { "type": "signal_loss", "signal_loss": { "timeoutMinutes": 20, "timeout_minutes": 20 } } } }
    ]});
    assert_eq!(
        unknown_keys_in_rule("composite", &params),
        vec![
            unknown("composite[0].threshold", "/conditions/0", "note"),
            unknown(
                "composite[0].threshold",
                "/conditions/0/Threshold",
                "for_minutes"
            ),
            unknown(
                "composite[1].not[0].signal_loss",
                "/conditions/1/not/child/signal_loss",
                "timeoutMinutes"
            ),
        ]
    );
    assert_eq!(
        validate_rule("composite", &params)
            .iter()
            .filter(|e| e.reason == Reason::UnknownField)
            .count(),
        3
    );
    assert_eq!(
        unknown_keys_in_rule("signal_loss", &json!({ "timeoutMinutes": 20 })),
        vec![unknown("signal_loss", "", "timeoutMinutes")]
    );
    assert_eq!(
        unknown_keys_in_node(
            &composite(
                "and",
                json!([
                    low(),
                    leaf("iob", json!({ "operator": ">", "value": 1, "units": "U" }))
                ])
            ),
            "snooze"
        ),
        vec![unknown(
            "snooze[1].iob",
            "/composite/conditions/1/iob",
            "units"
        )]
    );
    assert_eq!(
        unknown_keys_in_rule(
            "composite",
            &json!({ "operator": "and", "conditions": [low()] })
        ),
        vec![]
    );
}

#[test]
fn saving_accepts_property_names_in_any_case_and_other_kinds_payloads() {
    let params = json!({ "operator": "and", "conditions": [
        { "Type": "threshold", "THRESHOLD": { "Direction": "below", "VALUE": 70 },
          "iob": { "operator": ">", "value": 1 } }
    ]});
    assert_eq!(validate_rule("composite", &params), vec![]);
}

#[test]
fn saving_rejects_missing_operands_that_would_read_as_defaults() {
    let cases: Vec<(&str, Value, Reason)> = vec![
        (
            "rate_of_change",
            json!({ "direction": "falling" }),
            Reason::FieldMissing("rate"),
        ),
        (
            "staleness",
            json!({ "operator": ">=" }),
            Reason::FieldMissing("value"),
        ),
        (
            "predicted",
            json!({ "operator": "<", "value": 70 }),
            Reason::FieldMissing("within_minutes"),
        ),
        (
            "iob",
            json!({ "operator": ">" }),
            Reason::FieldMissing("value"),
        ),
        (
            "alert_state",
            json!({ "state": "firing" }),
            Reason::FieldMissing("alert_id"),
        ),
        (
            "loop_stale",
            json!({ "operator": ">" }),
            Reason::FieldMissing("minutes"),
        ),
        (
            "override_active",
            json!({}),
            Reason::FieldMissing("is_active"),
        ),
        (
            "sleep_session_active",
            json!({}),
            Reason::FieldMissing("is_active"),
        ),
        (
            "temp_basal",
            json!({ "operator": ">", "value": 1 }),
            Reason::FieldMissing("metric"),
        ),
        (
            "time_since_last_carb",
            json!({ "minutes": 30 }),
            Reason::FieldMissing("operator"),
        ),
        (
            "pump_state",
            json!({ "is_active": false }),
            Reason::FieldMissing("mode"),
        ),
        (
            "state_span_active",
            json!({ "category": "Override" }),
            Reason::FieldMissing("is_active"),
        ),
        (
            "tracker_age",
            json!({ "operator": ">=", "minutes": 30 }),
            Reason::FieldMissing("tracker_definition_id"),
        ),
        ("trend", json!({}), Reason::FieldMissing("bucket")),
    ];
    for (kind, payload, reason) in cases {
        assert_eq!(issues_of(kind, payload), at_leaf(kind, &[reason]), "{kind}");
    }
}

#[test]
fn saving_rejects_enum_values_no_member_has() {
    let cases: Vec<(&str, Value, Reason)> = vec![
        (
            "trend",
            json!({ "bucket": "sideways" }),
            Reason::UnknownValue("bucket"),
        ),
        (
            "temp_basal",
            json!({ "metric": 7, "operator": ">", "value": 1 }),
            Reason::UnknownValue("metric"),
        ),
        (
            "glucose_bucket",
            json!({ "buckets": ["low", 42] }),
            Reason::UnknownValue("buckets"),
        ),
        (
            "day_of_week",
            json!({ "days": [9] }),
            Reason::UnknownValue("days"),
        ),
        (
            "pump_state",
            json!({ "mode": 42, "is_active": false }),
            Reason::UnknownValue("mode"),
        ),
        (
            "state_span_active",
            json!({ "category": 99, "is_active": true }),
            Reason::UnknownValue("category"),
        ),
    ];
    for (kind, payload, reason) in cases {
        assert_eq!(issues_of(kind, payload), at_leaf(kind, &[reason]), "{kind}");
    }
}

#[test]
fn saving_rejects_empty_lists() {
    for (kind, field, payload) in [
        ("glucose_bucket", "buckets", json!({ "buckets": [] })),
        ("glucose_bucket", "buckets", json!({})),
        ("day_of_week", "days", json!({ "days": [] })),
        ("day_of_week", "days", json!({ "days": null })),
    ] {
        assert_eq!(
            issues_of(kind, payload),
            at_leaf(kind, &[Reason::ListEmpty(field)]),
            "{kind}"
        );
    }
}

#[test]
fn saving_rejects_time_windows_that_never_open() {
    assert_eq!(
        issues_of("time_of_day", json!({ "from": "9:00", "to": "17:00" })),
        at_leaf("time_of_day", &[Reason::InvalidTime("from")])
    );
    assert_eq!(
        issues_of("time_of_day", json!({ "from": "09:00", "to": "24:00" })),
        at_leaf("time_of_day", &[Reason::InvalidTime("to")])
    );
    assert_eq!(
        issues_of("time_of_day", json!({ "from": "22:00" })),
        at_leaf("time_of_day", &[Reason::FieldMissing("to")])
    );
    assert_eq!(
        issues_of("time_of_day", json!({ "from": "08:00", "to": "08:00" })),
        at_leaf("time_of_day", &[Reason::EmptyWindow])
    );
    assert_eq!(
        issues_of(
            "time_of_day",
            json!({ "from": "22:00", "to": "06:00", "timezone": null })
        ),
        vec![]
    );
}

#[test]
fn saving_rejects_a_generic_state_span_on_the_pump_mode_category() {
    let pump_mode = json!({ "category": "PumpMode", "is_active": true });
    assert_eq!(
        validate_rule("state_span_active", &pump_mode),
        vec![issue("state_span_active", Reason::PumpModeCategory)]
    );
    let nested = json!({ "minutes": 10, "child": { "type": "not", "not": { "child":
        leaf("state_span_active", json!({ "category": 0, "is_active": false })) } } });
    assert_eq!(
        validate_rule("sustained", &nested),
        vec![issue(
            "sustained[0].not[0].state_span_active",
            Reason::PumpModeCategory
        )]
    );
    assert_eq!(Reason::PumpModeCategory.field(), Some("category"));
}

#[test]
fn saving_rejects_durations_that_leave_a_leaf_never_or_always_true() {
    for minutes in [0, -5] {
        assert_eq!(
            issues_of("signal_loss", json!({ "timeout_minutes": minutes })),
            at_leaf(
                "signal_loss",
                &[Reason::MinutesNotPositive("timeout_minutes")]
            ),
            "{minutes}"
        );
        assert_eq!(
            issues_of(
                "predicted",
                json!({ "operator": "<=", "value": 70, "within_minutes": minutes })
            ),
            at_leaf("predicted", &[Reason::MinutesNotPositive("within_minutes")]),
            "{minutes}"
        );
    }
    for (kind, payload, field) in [
        (
            "staleness",
            json!({ "operator": ">", "value": -1 }),
            "value",
        ),
        (
            "loop_stale",
            json!({ "operator": "<", "minutes": -10 }),
            "minutes",
        ),
        (
            "loop_enaction_stale",
            json!({ "operator": ">=", "minutes": -10 }),
            "minutes",
        ),
        (
            "time_since_last_carb",
            json!({ "operator": ">=", "minutes": -1 }),
            "minutes",
        ),
        (
            "time_since_last_bolus",
            json!({ "operator": "<", "minutes": -1 }),
            "minutes",
        ),
    ] {
        assert_eq!(
            issues_of(kind, payload),
            at_leaf(kind, &[Reason::MinutesNegative(field)]),
            "{kind}"
        );
    }
    for (kind, payload) in [
        ("staleness", json!({ "operator": ">", "value": 0 })),
        ("loop_stale", json!({ "operator": ">", "minutes": 0 })),
        (
            "time_since_last_carb",
            json!({ "operator": ">=", "minutes": 0 }),
        ),
        (
            "tracker_age",
            json!({ "tracker_definition_id": "00000000-0000-0000-0000-0000000000bb",
                    "operator": ">=", "minutes": -60 }),
        ),
        (
            "pump_suspended",
            json!({ "is_active": true, "for_minutes": -5 }),
        ),
    ] {
        assert_eq!(issues_of(kind, payload), vec![], "{kind}");
    }
    assert_eq!(
        Reason::MinutesNotPositive("timeout_minutes").field(),
        Some("timeout_minutes")
    );
    assert!(!Reason::MinutesNegative("minutes").fails_evaluation());
}

#[test]
fn save_only_reasons_do_not_fail_evaluation() {
    let tree = composite(
        "or",
        json!([
            leaf("signal_loss", json!({ "timeoutMinutes": 20 })),
            leaf("time_of_day", json!({ "from": "9:00", "to": "9:00" })),
            leaf("day_of_week", json!({ "days": [] })),
            leaf("state_span_active", json!({ "category": "PumpMode" })),
            leaf("trend", json!({ "bucket": "sideways" })),
        ]),
    );
    assert!(Node::parse(&tree).is_ok());
    for reason in [
        Reason::UnknownField,
        Reason::FieldMissing("value"),
        Reason::UnknownValue("mode"),
        Reason::InvalidTime("from"),
        Reason::EmptyWindow,
        Reason::ListEmpty("days"),
        Reason::PumpModeCategory,
    ] {
        assert!(!reason.fails_evaluation(), "{reason:?}");
    }
}

/// The shapes the producers of stored trees write: the rule editor's
/// defaults (`makeDefault` in the web app's `alerts/types.ts`), the demo and
/// tracker-sync seeds, and the alerts-redesign and sleep-conversion
/// migrations. None may be rejected on save.
#[test]
fn every_producer_shape_saves() {
    let tracker = "00000000-0000-0000-0000-0000000000bb";
    let alert = "00000000-0000-0000-0000-0000000000aa";
    let editor = vec![
        leaf("threshold", json!({ "direction": "below", "value": 70 })),
        leaf(
            "rate_of_change",
            json!({ "direction": "falling", "rate": 3 }),
        ),
        leaf("staleness", json!({ "operator": ">=", "value": 15 })),
        leaf(
            "predicted",
            json!({ "operator": "<=", "value": 70, "within_minutes": 30 }),
        ),
        leaf("trend", json!({ "bucket": "falling" })),
        leaf(
            "time_of_day",
            json!({ "from": "22:00", "to": "06:00", "timezone": "Australia/Sydney" }),
        ),
        leaf("iob", json!({ "operator": ">=", "value": 1 })),
        leaf("cob", json!({ "operator": ">=", "value": 10 })),
        leaf("reservoir", json!({ "operator": "<=", "value": 10 })),
        leaf("site_age", json!({ "operator": ">=", "value": 72 })),
        leaf("sensor_age", json!({ "operator": ">=", "value": 10 })),
        leaf(
            "alert_state",
            json!({ "alert_id": alert, "state": "firing" }),
        ),
        leaf(
            "alert_state",
            json!({ "alert_id": alert, "state": "acknowledged", "for_minutes": null }),
        ),
        leaf("loop_stale", json!({ "operator": ">", "minutes": 15 })),
        leaf(
            "loop_enaction_stale",
            json!({ "operator": ">", "minutes": 15 }),
        ),
        leaf("pump_suspended", json!({ "is_active": true })),
        leaf("pump_battery", json!({ "operator": "<=", "value": 20 })),
        leaf(
            "temp_basal",
            json!({ "metric": "rate", "operator": ">=", "value": 1 }),
        ),
        leaf("uploader_battery", json!({ "operator": "<=", "value": 20 })),
        leaf("override_active", json!({ "is_active": true })),
        leaf(
            "sensitivity_ratio",
            json!({ "operator": "<", "value": 0.8 }),
        ),
        leaf("do_not_disturb", json!({ "is_active": false })),
        leaf("signal_loss", json!({ "timeout_minutes": 30 })),
        leaf("glucose_bucket", json!({ "buckets": ["low"] })),
        leaf(
            "time_since_last_carb",
            json!({ "operator": ">=", "minutes": 30 }),
        ),
        leaf(
            "time_since_last_bolus",
            json!({ "operator": ">=", "minutes": 60 }),
        ),
        leaf("day_of_week", json!({ "days": ["monday"] })),
        leaf(
            "pump_state",
            json!({ "mode": "Suspended", "is_active": true }),
        ),
        leaf(
            "state_span_active",
            json!({ "category": "Override", "is_active": true }),
        ),
        leaf("sleep_session_active", json!({ "is_active": true })),
        leaf(
            "tracker_age",
            json!({ "tracker_definition_id": tracker, "operator": ">=", "minutes": 0 }),
        ),
        leaf("sustained", json!({ "minutes": 15, "child": low() })),
        leaf("not", json!({ "child": low() })),
    ];
    let body = json!({ "operator": "and", "conditions": editor });
    assert_eq!(validate_rule("composite", &body), vec![]);

    // Rules the editor saved before it stripped `_uid` from a group's children.
    let with_uids = json!({ "operator": "or", "conditions": [
        { "type": "threshold", "_uid": "4b1c", "threshold": { "direction": "below", "value": 70 } },
        { "type": "not", "_uid": "4b1d", "not": { "child":
            { "type": "trend", "_uid": "4b1e", "trend": { "bucket": "flat" } } } }
    ]});
    assert_eq!(validate_rule("composite", &with_uids), vec![]);
    assert_eq!(validate_node(&leaf("composite", body), "snooze"), vec![]);

    let staleness = leaf("staleness", json!({ "operator": ">", "value": 15 }));
    for (kind, payload) in [
        ("threshold", json!({ "direction": "below", "value": 55 })),
        ("signal_loss", json!({ "timeout_minutes": 30 })),
        (
            "tracker_age",
            json!({ "tracker_definition_id": tracker, "operator": ">=", "minutes": 90 }),
        ),
        ("reservoir", json!({ "operator": "<", "value": 20 })),
        ("sleep_session_active", json!({ "is_active": true })),
        ("sustained", json!({ "minutes": 10, "child": staleness })),
    ] {
        assert_eq!(validate_rule(kind, &payload), vec![], "{kind}");
    }
}
