//! `nocturne_alerts_validate` through the C ABI, and the evaluate envelopes'
//! rejection of trees that cannot be evaluated.

#![allow(
    unsafe_code,
    clippy::undocumented_unsafe_blocks,
    reason = "tests call the C ABI"
)]

use std::ffi::{CStr, CString};

use serde_json::{Value, json};

use crate::{
    nocturne_alerts_evaluate, nocturne_alerts_evaluate_node, nocturne_alerts_free_string,
    nocturne_alerts_validate,
};

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

fn validate(request: Value) -> Value {
    call(nocturne_alerts_validate, &request)
}

fn low() -> Value {
    json!({ "type": "threshold", "threshold": { "operator": "<", "value": 70 } })
}

/// `node` as a stored root, naming schema 2.
fn rooted(node: Value) -> Value {
    let Value::Object(fields) = node else {
        return node;
    };
    let mut root = serde_json::Map::new();
    root.insert("schema_version".to_owned(), json!(2));
    root.extend(fields);
    Value::Object(root)
}

#[test]
fn validate_accepts_a_well_formed_rule() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "composite",
        "condition_params": { "schema_version": 2, "operator": "and", "conditions": [low()] },
        "auto_resolve_params": { "schema_version": 2, "type": "threshold", "threshold": { "operator": ">", "value": 90 } },
        "snooze_conditions": [
            { "schema_version": 2, "type": "trend", "trend": { "bucket": "rising" } },
            rooted(low()),
        ],
    }));
    assert_eq!(
        response,
        json!({ "schema_version": 1, "ok": true, "valid": true, "issues": [] })
    );
}

#[test]
fn validate_reports_issues_per_scope_with_paths() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "composite",
        "condition_params": { "schema_version": 2, "operator": "and", "conditions": [low(), null] },
        "auto_resolve_params": { "schema_version": 2, "type": "not", "not": {} },
        "snooze_conditions": [rooted(low()), { "schema_version": 2, "type": "RateOfChange" }],
    }));
    assert_eq!(response["ok"], json!(true));
    assert_eq!(response["valid"], json!(false));
    assert_eq!(
        response["issues"],
        json!([
            { "scope": "condition", "path": "composite[1].", "reason": "condition_missing", "field": null },
            { "scope": "auto_resolve", "path": "auto_resolve", "reason": "field_missing", "field": "child" },
            { "scope": "snooze", "path": "snooze[1].RateOfChange", "reason": "unknown_kind", "field": "type" },
        ])
    );
}

#[test]
fn validate_reports_a_root_without_schema_2_once_per_scope() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "composite",
        "condition_params": { "operator": "and", "conditions": [low(), null] },
        "auto_resolve_params": { "schema_version": 1, "type": "not", "not": {} },
        "snooze_conditions": [rooted(low()), low()],
    }));
    assert_eq!(
        response["issues"],
        json!([
            { "scope": "condition", "path": "composite", "reason": "unsupported_schema", "field": "schema_version" },
            { "scope": "auto_resolve", "path": "auto_resolve", "reason": "unsupported_schema", "field": "schema_version" },
            { "scope": "snooze", "path": "snooze", "reason": "unsupported_schema", "field": "schema_version" },
        ])
    );
}

#[test]
fn validate_reports_schema_version_below_a_root_as_an_unknown_field() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "composite",
        "condition_params": { "schema_version": 2, "operator": "and", "conditions": [rooted(low())] },
    }));
    assert_eq!(
        response["issues"],
        json!([
            { "scope": "condition", "path": "composite[0].threshold", "reason": "unknown_field", "field": null },
        ])
    );
}

#[test]
fn validate_skips_null_auto_resolve_and_empty_snooze_conditions() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "threshold",
        "condition_params": { "schema_version": 2, "operator": "<", "value": 70 },
        "auto_resolve_params": null,
        "snooze_conditions": [],
    }));
    assert_eq!(response["valid"], json!(true));
}

#[test]
fn validate_rejects_a_bad_envelope() {
    let response = validate(json!({ "schema_version": 2, "condition_type": "threshold" }));
    assert_eq!(response["ok"], json!(false));
    assert!(
        response["error"]
            .as_str()
            .unwrap()
            .contains("unsupported schema_version")
    );
}

#[test]
fn evaluate_rejects_a_rule_body_that_cannot_be_evaluated() {
    let request = |condition_params: Value| {
        call(
            nocturne_alerts_evaluate,
            &json!({
                "schema_version": 1,
                "rule": {
                    "id": "00000000-0000-0000-0000-000000000001",
                    "condition_type": "composite",
                    "condition_params": condition_params
                },
                "context": {},
                "now": "2026-01-05T12:00:00Z",
            }),
        )
    };
    let conditions = json!([low(), { "type": "composite" }]);
    let response =
        request(json!({ "schema_version": 2, "operator": "or", "conditions": conditions }));
    assert_eq!(response["ok"], json!(false));
    assert_eq!(
        response["error"],
        json!(
            "malformed condition_params for 'composite': payload_missing at 'composite[1].composite'"
        )
    );
    assert_eq!(
        request(json!({ "operator": "or", "conditions": [low()] }))["error"],
        json!(
            "malformed condition_params for 'composite': unsupported_schema at 'composite' (field 'schema_version')"
        )
    );
}

#[test]
fn evaluate_node_rejects_a_tree_that_cannot_be_evaluated_under_its_root() {
    let request = |node: Value| {
        call(
            nocturne_alerts_evaluate_node,
            &json!({
                "schema_version": 1,
                "rule_id": "00000000-0000-0000-0000-000000000001",
                "node": node,
                "root": "snooze",
                "context": {},
                "now": "2026-01-05T12:00:00Z",
            }),
        )
    };
    let node = json!({ "type": "composite", "composite": { "operator": "and", "conditions": [
        { "type": "alert_state", "alert_state": { "alert_id": "00000000-0000-0000-0000-0000000000aa" } }
    ] } });
    assert_eq!(
        request(rooted(node.clone()))["error"],
        json!("malformed condition node: field_missing at 'snooze[0].alert_state' (field 'state')")
    );
    assert_eq!(
        request(node)["error"],
        json!("malformed condition node: unsupported_schema at 'snooze' (field 'schema_version')")
    );
}
