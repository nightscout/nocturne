//! `migrate_v1_to_v2` (engine-semantics.md §1.5): each schema 1 shape and the
//! schema 2 shape it becomes, the rounding of ages, what it refuses, and that
//! it can run again.
#![allow(clippy::expect_used, clippy::panic, reason = "test code")]

use nocturne_alerts_core::migrate::{Migrated, Migration, TreeRoot, migrate_v1_to_v2};
use nocturne_alerts_core::model::{ConditionKind, Node, Reason, parse_payload};
use serde_json::{Value, json};

fn migrated(tree: &Value, root: TreeRoot<'_>) -> Migrated {
    match migrate_v1_to_v2(tree, root) {
        Ok(Migration::Migrated(m)) => m,
        other => panic!("expected a migration, got {other:?}"),
    }
}

fn body(kind: &str, params: Value) -> Migrated {
    let m = migrated(&params, TreeRoot::Body(kind));
    let kind = ConditionKind::from_wire(kind).expect("a kind");
    parse_payload(kind, &m.tree).expect("the migrated body reads as schema 2");
    m
}

fn node(tree: Value) -> Migrated {
    let m = migrated(&tree, TreeRoot::Node("auto_resolve"));
    Node::parse_rooted(&m.tree, "auto_resolve").expect("the migrated node reads as schema 2");
    m
}

fn refused(tree: &Value, root: TreeRoot<'_>) -> (String, Reason) {
    match migrate_v1_to_v2(tree, root) {
        Err(e) => (e.path, e.reason),
        Ok(other) => panic!("expected a refusal, got {other:?}"),
    }
}

#[test]
fn a_threshold_direction_becomes_its_strict_operator() {
    let below = body(
        "threshold",
        json!({"direction": "below", "value": 70, "clear_margin": 10}),
    );
    assert_eq!(
        below.tree,
        json!({"schema_version": 2, "value": 70, "clear_margin": 10, "operator": "<"})
    );
    assert!(!below.rounded);
    let above = body(
        "threshold",
        json!({"direction": "above", "value_from": {"fact": "target_high", "offset": 20}}),
    );
    assert_eq!(above.tree["operator"], json!(">"));
    assert_eq!(
        above.tree["value_from"],
        json!({"fact": "target_high", "offset": 20})
    );
}

#[test]
fn the_schema_is_named_first() {
    let m = body("threshold", json!({"direction": "below", "value": 70}));
    let keys: Vec<&String> = m.tree.as_object().expect("an object").keys().collect();
    assert_eq!(keys.first().map(|k| k.as_str()), Some("schema_version"));
}

#[test]
fn a_rate_of_change_becomes_a_signed_inclusive_comparison() {
    assert_eq!(
        body(
            "rate_of_change",
            json!({"direction": "falling", "rate": 2.5, "clear_margin": 1})
        )
        .tree,
        json!({"schema_version": 2, "clear_margin": 1, "operator": "<=", "value": -2.5})
    );
    assert_eq!(
        body("rate_of_change", json!({"direction": "rising", "rate": 3})).tree,
        json!({"schema_version": 2, "operator": ">=", "value": 3})
    );
    assert_eq!(
        body("rate_of_change", json!({"direction": "falling", "rate": 0})).tree["value"],
        json!(0)
    );
}

#[test]
fn staleness_names_its_minutes() {
    assert_eq!(
        body("staleness", json!({"operator": ">=", "value": 20})).tree,
        json!({"schema_version": 2, "operator": ">=", "minutes": 20})
    );
}

#[test]
fn ages_become_whole_minutes() {
    let site = body("site_age", json!({"operator": ">=", "value": 72}));
    assert_eq!(
        site.tree,
        json!({"schema_version": 2, "operator": ">=", "minutes": 4320})
    );
    assert!(!site.rounded);
    assert_eq!(
        body("site_age", json!({"operator": ">", "value": 1.25})).tree["minutes"],
        json!(75)
    );
    let sensor = body("sensor_age", json!({"operator": ">=", "value": 9.5}));
    assert_eq!(sensor.tree["minutes"], json!(13680));
    assert!(!sensor.rounded);
}

#[test]
fn an_age_between_minutes_rounds_to_the_side_that_holds_no_later() {
    // 1.001 hours is 60.06 minutes.
    let at_least = body("site_age", json!({"operator": ">=", "value": 1.001}));
    assert_eq!(at_least.tree["minutes"], json!(60));
    assert!(at_least.rounded);
    let equal = body("site_age", json!({"operator": "==", "value": 1.001}));
    assert_eq!(equal.tree["minutes"], json!(60));
    let below = body("site_age", json!({"operator": "<", "value": 1.001}));
    assert_eq!(below.tree["minutes"], json!(61));
    assert!(below.rounded);
}

#[test]
fn under_a_not_an_age_rounds_the_other_way() {
    let leaf = |operator: &str| json!({"type": "site_age", "site_age": {"operator": operator, "value": 1.001}});
    let negated = node(json!({"type": "not", "not": {"child": leaf(">=")}}));
    assert_eq!(
        negated.tree["not"]["child"]["site_age"]["minutes"],
        json!(61)
    );
    let twice = node(json!({"type": "not", "not": {"child":
        {"type": "not", "not": {"child": leaf(">=")}}}}));
    assert_eq!(
        twice.tree["not"]["child"]["not"]["child"]["site_age"]["minutes"],
        json!(60)
    );
    let sustained = node(json!({"type": "not", "not": {"child":
        {"type": "sustained", "sustained": {"minutes": 5, "child": leaf("<")}}}}));
    assert_eq!(
        sustained.tree["not"]["child"]["sustained"]["child"]["site_age"]["minutes"],
        json!(60)
    );
}

#[test]
fn an_age_past_the_minute_range_is_held_at_its_end_for_review() {
    let m = body("sensor_age", json!({"operator": ">", "value": 10_000_000}));
    assert_eq!(m.tree["minutes"], json!(i32::MAX));
    assert!(m.rounded);
}

#[test]
fn every_node_of_a_tree_is_migrated_and_nothing_else_changes() {
    let tree = json!({
        "type": "composite",
        "_uid": "root",
        "composite": {"operator": "or", "conditions": [
            {"type": "threshold", "_uid": "a", "threshold": {"direction": "below", "value": 55}},
            {"type": "sustained", "sustained": {"minutes": 15, "child":
                {"type": "rate_of_change", "rate_of_change": {"direction": "rising", "rate": 2}}}},
            {"type": "not", "not": {"child":
                {"type": "staleness", "staleness": {"operator": ">", "value": 30}}}},
            {"type": "iob", "iob": {"operator": ">", "value": 3.5}},
        ]},
    });
    let m = node(tree);
    assert_eq!(
        m.tree,
        json!({
            "schema_version": 2,
            "type": "composite",
            "_uid": "root",
            "composite": {"operator": "or", "conditions": [
                {"type": "threshold", "_uid": "a", "threshold": {"value": 55, "operator": "<"}},
                {"type": "sustained", "sustained": {"minutes": 15, "child":
                    {"type": "rate_of_change", "rate_of_change": {"operator": ">=", "value": 2}}}},
                {"type": "not", "not": {"child":
                    {"type": "staleness", "staleness": {"operator": ">", "minutes": 30}}}},
                {"type": "iob", "iob": {"operator": ">", "value": 3.5}},
            ]},
        })
    );
}

#[test]
fn a_composite_body_migrates_its_children() {
    let m = body(
        "composite",
        json!({"operator": "and", "conditions": [
            {"type": "threshold", "threshold": {"direction": "above", "value": 250}},
        ]}),
    );
    assert_eq!(m.tree["conditions"][0]["threshold"]["operator"], json!(">"));
    assert_eq!(m.tree["conditions"][0].get("schema_version"), None);
}

#[test]
fn a_migrated_tree_is_current() {
    let m = body("threshold", json!({"direction": "below", "value": 70}));
    assert_eq!(
        migrate_v1_to_v2(&m.tree, TreeRoot::Body("threshold")),
        Ok(Migration::Current)
    );
    let n = node(json!({"type": "iob", "iob": {"operator": ">", "value": 2}}));
    assert_eq!(
        migrate_v1_to_v2(&n.tree, TreeRoot::Node("snooze")),
        Ok(Migration::Current)
    );
}

#[test]
fn a_kind_whose_shape_did_not_change_gains_only_the_schema() {
    let params = json!({"operator": ">=", "minutes": 45});
    let m = body("loop_stale", params);
    assert_eq!(
        m.tree,
        json!({"schema_version": 2, "operator": ">=", "minutes": 45})
    );
}

#[test]
fn a_missing_operand_stays_missing() {
    let m = migrated(&json!({"value": 70}), TreeRoot::Body("threshold"));
    assert_eq!(m.tree, json!({"schema_version": 2, "value": 70}));
}

#[test]
fn what_schema_1_could_not_read_stays_as_it_is() {
    let cases = [
        (
            json!({"direction": "sideways", "value": 70}),
            "threshold",
            Reason::UnknownValue("direction"),
        ),
        (
            json!({"direction": 1, "value": 70}),
            "threshold",
            Reason::InvalidField("direction"),
        ),
        (
            json!({"direction": "below", "operator": "<", "value": 70}),
            "threshold",
            Reason::UnknownField,
        ),
        (
            json!({"rate": 2}),
            "rate_of_change",
            Reason::FieldMissing("direction"),
        ),
        (
            json!({"direction": "falling", "rate": "2"}),
            "rate_of_change",
            Reason::InvalidField("rate"),
        ),
        (
            json!({"operator": ">", "value": 1, "minutes": 1}),
            "staleness",
            Reason::UnknownField,
        ),
        (
            json!({"operator": ">", "value": "72"}),
            "site_age",
            Reason::InvalidField("value"),
        ),
        (
            json!({"value": 1.001}),
            "site_age",
            Reason::FieldMissing("operator"),
        ),
        (
            json!({"operator": "gt", "value": 1.001}),
            "site_age",
            Reason::UnknownValue("operator"),
        ),
        (
            json!({"operator": "and"}),
            "composite",
            Reason::ListEmpty("conditions"),
        ),
        (
            json!({"operator": "and", "conditions": [null]}),
            "composite",
            Reason::ConditionMissing,
        ),
        (
            json!({"operator": "and", "conditions": [{"threshold": {}}]}),
            "composite",
            Reason::TypeMissing,
        ),
        (
            json!({"operator": "and", "conditions": [{"type": "warp"}]}),
            "composite",
            Reason::UnknownKind,
        ),
        (
            json!({"operator": "and", "conditions": [{"type": "iob"}]}),
            "composite",
            Reason::PayloadMissing,
        ),
        (
            json!({"schema_version": 3, "operator": "<", "value": 70}),
            "threshold",
            Reason::UnsupportedSchema,
        ),
        (
            json!({"schema_version": "2", "operator": "<", "value": 70}),
            "threshold",
            Reason::UnsupportedSchema,
        ),
        (json!([1]), "threshold", Reason::NotAnObject),
        (Value::Null, "threshold", Reason::PayloadMissing),
        (json!({"value": 70}), "warp", Reason::UnknownKind),
    ];
    for (tree, kind, reason) in cases {
        assert_eq!(refused(&tree, TreeRoot::Body(kind)).1, reason, "{tree}");
    }
}

#[test]
fn a_refusal_names_the_node_it_is_on() {
    let tree = json!({"type": "not", "not": {"child":
        {"type": "threshold", "threshold": {"direction": "up", "value": 70}}}});
    assert_eq!(
        refused(&tree, TreeRoot::Node("snooze")),
        (
            "snooze[0].threshold".to_owned(),
            Reason::UnknownValue("direction")
        )
    );
}
