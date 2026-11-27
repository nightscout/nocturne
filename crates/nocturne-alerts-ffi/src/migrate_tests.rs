//! `nocturne_alerts_migrate` through the C ABI: one stored schema 1 tree
//! rewritten as schema 2 (engine-semantics.md §1.5).

#![allow(
    unsafe_code,
    clippy::undocumented_unsafe_blocks,
    reason = "tests call the C ABI"
)]

use std::ffi::{CStr, CString};

use serde_json::{Value, json};

use crate::{nocturne_alerts_free_string, nocturne_alerts_migrate, nocturne_alerts_validate};

fn call(
    f: unsafe extern "C" fn(*const std::ffi::c_char) -> *mut std::ffi::c_char,
    input: &Value,
) -> Value {
    let c_input = CString::new(input.to_string()).expect("test input has no NUL");
    unsafe {
        let ptr = f(c_input.as_ptr());
        assert!(!ptr.is_null(), "FFI returned null pointer");
        let out = CStr::from_ptr(ptr)
            .to_str()
            .expect("valid UTF-8")
            .to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).expect("FFI returned valid JSON")
    }
}

fn migrate(request: Value) -> Value {
    call(nocturne_alerts_migrate, &request)
}

fn body(condition_type: &str, tree: Value) -> Value {
    migrate(json!({ "schema_version": 1, "condition_type": condition_type, "tree": tree }))
}

fn node(root: &str, tree: Value) -> Value {
    migrate(json!({ "schema_version": 1, "root": root, "tree": tree }))
}

#[test]
fn a_schema_1_body_is_migrated_to_schema_2() {
    let response = body("threshold", json!({ "direction": "below", "value": 70 }));
    assert_eq!(
        response,
        json!({
            "schema_version": 1,
            "ok": true,
            "outcome": "migrated",
            "tree": { "schema_version": 2, "operator": "<", "value": 70 },
            "rounded": false,
        })
    );
}

#[test]
fn a_migrated_body_validates() {
    let migrated = body(
        "composite",
        json!({ "operator": "and", "conditions": [
            { "type": "staleness", "staleness": { "operator": ">=", "value": 20 } },
            { "type": "site_age", "site_age": { "operator": ">", "value": 1.5 } },
        ] }),
    );
    assert_eq!(migrated["rounded"], json!(false));
    assert_eq!(
        migrated["tree"]["conditions"],
        json!([
            { "type": "staleness", "staleness": { "operator": ">=", "minutes": 20 } },
            { "type": "site_age", "site_age": { "operator": ">", "minutes": 90 } },
        ])
    );
    let validated = call(
        nocturne_alerts_validate,
        &json!({
            "schema_version": 1,
            "condition_type": "composite",
            "condition_params": migrated["tree"],
        }),
    );
    assert_eq!(validated["valid"], json!(true), "{validated}");
}

#[test]
fn an_age_that_is_not_whole_minutes_is_rounded_and_reported() {
    let site = body("site_age", json!({ "operator": ">=", "value": 0.01 }));
    assert_eq!(site["outcome"], json!("migrated"));
    assert_eq!(
        site["tree"],
        json!({ "schema_version": 2, "operator": ">=", "minutes": 0 })
    );
    assert_eq!(site["rounded"], json!(true));

    let sensor = body("sensor_age", json!({ "operator": "<", "value": 1.0001 }));
    assert_eq!(
        sensor["tree"],
        json!({ "schema_version": 2, "operator": "<", "minutes": 1441 })
    );
    assert_eq!(sensor["rounded"], json!(true));
}

#[test]
fn a_schema_1_node_is_migrated_with_the_name_only_at_its_root() {
    let response = node(
        "auto_resolve",
        json!({ "type": "composite", "_uid": "a", "composite": { "operator": "or", "conditions": [
            { "type": "threshold", "threshold": { "direction": "above", "value": 90 } },
            { "type": "not", "not": { "child":
                { "type": "rate_of_change", "rate_of_change": { "direction": "falling", "rate": 2 } } } },
        ] } }),
    );
    assert_eq!(response["ok"], json!(true));
    assert_eq!(response["outcome"], json!("migrated"));
    assert_eq!(response["rounded"], json!(false));
    assert_eq!(
        response["tree"],
        json!({ "schema_version": 2, "type": "composite", "_uid": "a", "composite": {
            "operator": "or", "conditions": [
                { "type": "threshold", "threshold": { "operator": ">", "value": 90 } },
                { "type": "not", "not": { "child":
                    { "type": "rate_of_change", "rate_of_change": { "operator": "<=", "value": -2 } } } },
            ] } })
    );
}

#[test]
fn a_schema_2_tree_is_current() {
    let current = json!({ "schema_version": 1, "ok": true, "outcome": "current" });
    assert_eq!(
        body(
            "threshold",
            json!({ "schema_version": 2, "operator": "<", "value": 70 })
        ),
        current
    );
    assert_eq!(
        node(
            "snooze",
            json!({ "schema_version": 2, "type": "trend", "trend": { "bucket": "rising" } })
        ),
        current
    );
}

#[test]
fn a_tree_that_cannot_be_read_as_schema_1_fails_with_its_issue() {
    assert_eq!(
        body("rate_of_change", json!({ "rate": 2 })),
        json!({
            "schema_version": 1,
            "ok": true,
            "outcome": "failed",
            "issue": { "path": "rate_of_change", "reason": "field_missing", "field": "direction" },
        })
    );
    assert_eq!(
        node(
            "auto_resolve",
            json!({ "type": "not", "not": { "child":
                { "type": "threshold", "threshold": { "direction": "sideways", "value": 70 } } } })
        )["issue"],
        json!({ "path": "auto_resolve[0].threshold", "reason": "unknown_value", "field": "direction" })
    );
    assert_eq!(
        body(
            "threshold",
            json!({ "schema_version": 3, "operator": "<", "value": 70 })
        )["issue"],
        json!({ "path": "threshold", "reason": "unsupported_schema", "field": "schema_version" })
    );
}

#[test]
fn a_request_needs_exactly_one_of_condition_type_and_root() {
    for request in [
        json!({ "schema_version": 1, "condition_type": "threshold", "root": "snooze", "tree": {} }),
        json!({ "schema_version": 1, "tree": {} }),
    ] {
        let response = migrate(request);
        assert_eq!(response["ok"], json!(false));
        assert_eq!(
            response["error"],
            json!("exactly one of 'condition_type' and 'root' is required")
        );
    }
}

#[test]
fn migrate_rejects_a_bad_envelope() {
    let response =
        migrate(json!({ "schema_version": 2, "condition_type": "threshold", "tree": {} }));
    assert_eq!(response["ok"], json!(false));
    assert!(
        response["error"]
            .as_str()
            .unwrap()
            .contains("unsupported schema_version")
    );
}
