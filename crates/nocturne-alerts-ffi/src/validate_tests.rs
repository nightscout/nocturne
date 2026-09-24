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
    json!({ "type": "threshold", "threshold": { "direction": "below", "value": 70 } })
}

#[test]
fn validate_accepts_a_well_formed_rule() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "composite",
        "condition_params": { "operator": "and", "conditions": [low()] },
        "auto_resolve_params": { "type": "threshold", "threshold": { "direction": "above", "value": 90 } },
        "snooze_conditions": [{ "type": "trend", "trend": { "bucket": "rising" } }],
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
        "condition_params": { "operator": "and", "conditions": [low(), null] },
        "auto_resolve_params": { "type": "not", "not": {} },
        "snooze_conditions": [low(), { "type": "RateOfChange" }],
    }));
    assert_eq!(response["ok"], json!(true));
    assert_eq!(response["valid"], json!(false));
    assert_eq!(
        response["issues"],
        json!([
            { "scope": "condition", "path": "composite[1].", "reason": "condition_missing", "field": null },
            { "scope": "auto_resolve", "path": "auto_resolve", "reason": "child_missing", "field": "child" },
            { "scope": "snooze", "path": "snooze[1].RateOfChange", "reason": "non_canonical_type", "field": "type" },
            { "scope": "snooze", "path": "snooze[1].RateOfChange", "reason": "direction_missing", "field": "direction" },
        ])
    );
}

#[test]
fn validate_skips_null_auto_resolve_and_empty_snooze_conditions() {
    let response = validate(json!({
        "schema_version": 1,
        "condition_type": "threshold",
        "condition_params": { "direction": "below", "value": 70 },
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
    let response = call(
        nocturne_alerts_evaluate,
        &json!({
            "schema_version": 1,
            "rule": {
                "id": "00000000-0000-0000-0000-000000000001",
                "condition_type": "composite",
                "condition_params": { "operator": "or", "conditions": [low(), { "type": "composite" }] }
            },
            "context": {},
            "now": "2026-01-05T12:00:00Z",
        }),
    );
    assert_eq!(response["ok"], json!(false));
    assert_eq!(
        response["error"],
        json!(
            "malformed condition_params for 'composite': conditions_missing at 'composite[1].composite' (field 'conditions')"
        )
    );
}

#[test]
fn evaluate_node_rejects_a_tree_that_cannot_be_evaluated_under_its_root() {
    let response = call(
        nocturne_alerts_evaluate_node,
        &json!({
            "schema_version": 1,
            "rule_id": "00000000-0000-0000-0000-000000000001",
            "node": { "type": "composite", "composite": { "operator": "and", "conditions": [{ "type": "alert_state" }] } },
            "root": "snooze",
            "context": {},
            "now": "2026-01-05T12:00:00Z",
        }),
    );
    assert_eq!(
        response["error"],
        json!("malformed condition node: state_missing at 'snooze[0].alert_state' (field 'state')")
    );
}
