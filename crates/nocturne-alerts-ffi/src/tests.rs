//! FFI-boundary tests: drive the C ABI functions directly (pointers in,
//! pointers out) — the full golden corpus threaded through the evaluate
//! envelope, plus the error-envelope contract (null, invalid UTF-8, malformed
//! JSON, bad schema, panic path).

use std::ffi::{CStr, CString, c_char};
use std::fs;
use std::path::PathBuf;

use serde_json::{Map, Value, json};

use crate::{
    boundary, nocturne_alerts_classify, nocturne_alerts_describe, nocturne_alerts_evaluate,
    nocturne_alerts_evaluate_node, nocturne_alerts_free_string, nocturne_alerts_leaf_paths,
    nocturne_alerts_version,
};

/// Calls an FFI function with `input`, copies the result into a Rust string
/// and frees the native allocation.
fn call(f: unsafe extern "C" fn(*const c_char) -> *mut c_char, input: &str) -> String {
    let c_input = CString::new(input).expect("test input has no NUL");
    unsafe {
        let ptr = f(c_input.as_ptr());
        assert!(!ptr.is_null(), "FFI returned null pointer");
        let out = CStr::from_ptr(ptr)
            .to_str()
            .expect("valid UTF-8")
            .to_string();
        nocturne_alerts_free_string(ptr);
        out
    }
}

fn call_json(f: unsafe extern "C" fn(*const c_char) -> *mut c_char, input: &str) -> Value {
    serde_json::from_str(&call(f, input)).expect("FFI returned valid JSON")
}

fn evaluate(request: &Value) -> Value {
    call_json(nocturne_alerts_evaluate, &request.to_string())
}

// ---------------------------------------------------------------------------
// Version + free
// ---------------------------------------------------------------------------

#[test]
fn version_returns_crate_version() {
    unsafe {
        let ptr = nocturne_alerts_version();
        assert!(!ptr.is_null());
        let version = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        assert_eq!(version, env!("CARGO_PKG_VERSION"));
    }
}

#[test]
fn free_string_accepts_null() {
    unsafe { nocturne_alerts_free_string(std::ptr::null_mut()) };
}

// ---------------------------------------------------------------------------
// Corpus round-trip through the C ABI
// ---------------------------------------------------------------------------

#[derive(serde::Deserialize)]
struct ScenarioFile {
    name: String,
    rules: Vec<Value>,
    ticks: Vec<ScenarioTick>,
}

#[derive(serde::Deserialize)]
struct ScenarioTick {
    at: String,
    context: Value,
}

fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/Parity/AlertEngineCorpus")
        .canonicalize()
        .expect("corpus directory exists")
}

/// Lists every corpus scenario file (excluding the `.expected.json`
/// snapshots), sorted for determinism.
fn scenario_paths() -> Vec<PathBuf> {
    let mut paths: Vec<PathBuf> = fs::read_dir(corpus_dir())
        .expect("read corpus dir")
        .map(|e| e.expect("dir entry").path())
        .filter(|p| {
            p.extension().is_some_and(|ext| ext == "json")
                && !p
                    .file_name()
                    .is_some_and(|n| n.to_string_lossy().ends_with(".expected.json"))
        })
        .collect();
    paths.sort();
    paths
}

fn load_scenario(path: &PathBuf) -> (ScenarioFile, Value) {
    let scenario: ScenarioFile =
        serde_json::from_str(&fs::read_to_string(path).expect("read scenario"))
            .unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
    let expected_path = path.with_file_name(format!(
        "{}.expected.json",
        path.file_stem().unwrap().to_string_lossy()
    ));
    let expected: Value =
        serde_json::from_str(&fs::read_to_string(&expected_path).expect("read expected"))
            .expect("parse expected");
    (scenario, expected)
}

/// Drives one scenario through the evaluate-envelope functions (C ABI or
/// UniFFI), threading the timers/tracker state envelopes between ticks exactly
/// as a host would, and returns the assembled expected-file Value.
///
/// Two envelopes, because a host runs two: `evaluate` for the rule body on each
/// reading, and `evaluate_node` under `root: "snooze"` for the sweep's
/// smart-snooze predicate whenever the scenario configures snooze conditions.
fn run_scenario(
    scenario: &ScenarioFile,
    evaluate: impl Fn(&Value) -> Value,
    evaluate_node: impl Fn(&Value) -> Value,
) -> Value {
    // Per-rule persisted state, plus the shared next-excursion ordinal.
    let mut timers: Map<String, Value> = Map::new(); // rule id -> timers object
    let mut trackers: Map<String, Value> = Map::new(); // rule id -> tracker object
    let mut next_ordinal: u64 = 1;

    let ticks: Vec<Value> = scenario
        .ticks
        .iter()
        .map(|tick| {
            let rules: Vec<Value> = scenario
                .rules
                .iter()
                .map(|rule| {
                    let rule_id = rule["id"].as_str().expect("rule id").to_string();
                    let mut tracker = trackers
                        .get(&rule_id)
                        .and_then(|t| t.as_object().cloned())
                        .unwrap_or_default();
                    tracker.insert("next_excursion_ordinal".into(), json!(next_ordinal));

                    let request = json!({
                        "schema_version": 1,
                        "rule": rule,
                        "context": tick.context,
                        "now": tick.at,
                        "timers": timers.get(&rule_id).cloned().unwrap_or_else(|| json!({})),
                        "tracker": tracker,
                    });

                    let response = evaluate(&request);
                    assert_eq!(
                        response["ok"],
                        Value::Bool(true),
                        "scenario {} tick {} rule {}: {}",
                        scenario.name,
                        tick.at,
                        rule_id,
                        response["error"]
                    );

                    timers.insert(rule_id.clone(), response["timers"].clone());
                    trackers.insert(rule_id.clone(), response["tracker"].clone());
                    next_ordinal = response["tracker"]["next_excursion_ordinal"]
                        .as_u64()
                        .expect("next_excursion_ordinal present");

                    let mut result = response["result"].clone();
                    apply_snooze_step(
                        rule,
                        tick,
                        &rule_id,
                        &mut timers,
                        &mut result,
                        &evaluate_node,
                    );
                    result
                })
                .collect();
            json!({ "at": tick.at, "rules": rules })
        })
        .collect();

    json!({
        "schema_version": 1,
        "scenario": scenario.name,
        "ticks": ticks,
    })
}

/// Folds the rule's smart-snooze predicate into its result object: wraps the
/// configured conditions as `composite{and, conditions}`, evaluates them under
/// `root: "snooze"` against the same threaded timer state, and records
/// `snooze_extend` plus any timer ops after the rule body's.
///
/// A skipped rule (`signal_loss`) records nothing else, so there is nowhere to
/// put a snooze result. Production does *not* skip it; the generator rejects
/// that combination at authoring time, so no scenario reaches this branch with
/// conditions configured.
fn apply_snooze_step(
    rule: &Value,
    tick: &ScenarioTick,
    rule_id: &str,
    timers: &mut Map<String, Value>,
    result: &mut Value,
    evaluate_node: impl Fn(&Value) -> Value,
) {
    if result.get("skipped").is_some() {
        return;
    }
    let Some(conditions) = rule
        .get("snooze_conditions")
        .and_then(Value::as_array)
        .filter(|c| !c.is_empty())
    else {
        return;
    };

    let response = evaluate_node(&json!({
        "schema_version": 1,
        "rule_id": rule_id,
        "node": {
            "type": "composite",
            "composite": { "operator": "and", "conditions": conditions },
        },
        "root": "snooze",
        "context": tick.context,
        "now": tick.at,
        "timers": timers.get(rule_id).cloned().unwrap_or_else(|| json!({})),
    }));
    assert_eq!(
        response["ok"],
        Value::Bool(true),
        "snooze step for rule {rule_id} at {}: {}",
        tick.at,
        response["error"]
    );

    timers.insert(rule_id.to_string(), response["timers"].clone());

    let result = result.as_object_mut().expect("rule result is an object");
    result.insert("snooze_extend".into(), response["value"].clone());

    let ops = response["timer_ops"].as_array().expect("timer_ops array");
    if !ops.is_empty() {
        result
            .entry("timer_ops")
            .or_insert_with(|| json!([]))
            .as_array_mut()
            .expect("timer_ops is an array")
            .extend(ops.iter().cloned());
    }
}

/// Runs the full corpus through the evaluate envelopes and pins every scenario
/// against its committed `.expected.json` snapshot.
fn assert_corpus_round_trips(
    evaluate: impl Fn(&Value) -> Value,
    evaluate_node: impl Fn(&Value) -> Value,
) {
    let scenario_paths = scenario_paths();
    assert!(
        scenario_paths.len() >= 100,
        "expected >= 100 corpus scenarios, found {}",
        scenario_paths.len()
    );

    let mut failed: Vec<String> = Vec::new();
    for path in &scenario_paths {
        let (scenario, expected) = load_scenario(path);
        let actual = run_scenario(&scenario, &evaluate, &evaluate_node);
        if actual != expected {
            failed.push(format!(
                "scenario {}: FFI output differs from expected snapshot",
                scenario.name
            ));
        }
    }
    assert!(
        failed.is_empty(),
        "{} of {} scenarios diverged:\n{}",
        failed.len(),
        scenario_paths.len(),
        failed.join("\n")
    );
}

#[test]
fn corpus_round_trips_through_c_abi() {
    assert_corpus_round_trips(evaluate, evaluate_node);
}

#[test]
fn state_threading_survives_serialisation() {
    // A sustained rule needs its timer back on the next tick; a tracker in
    // hysteresis needs updated_at. Exercise both through two manual calls.
    let rule = json!({
        "id": "00000000-0000-0000-0000-00000000abcd",
        "condition_type": "sustained",
        "condition_params": {
            "minutes": 10,
            "child": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } }
        }
    });
    let context = json!({ "latest_value": 60, "latest_timestamp": "2026-01-05T12:00:00Z" });

    let first = evaluate(&json!({
        "schema_version": 1,
        "rule": rule,
        "context": context,
        "now": "2026-01-05T12:00:00Z",
    }));
    assert_eq!(first["ok"], Value::Bool(true));
    assert_eq!(first["result"]["root"], Value::Bool(false));
    assert_eq!(first["timers"]["sustained"], json!("2026-01-05T12:00:00Z"));
    assert_eq!(first["tracker"]["state"], json!("idle"));
    assert_eq!(first["tracker"]["next_excursion_ordinal"], json!(1));

    let second = evaluate(&json!({
        "schema_version": 1,
        "rule": rule,
        "context": context,
        "now": "2026-01-05T12:10:00Z",
        "timers": first["timers"],
        "tracker": first["tracker"],
    }));
    assert_eq!(second["ok"], Value::Bool(true));
    assert_eq!(second["result"]["root"], Value::Bool(true));
    assert_eq!(second["result"]["transition"], json!("opened"));
    assert_eq!(second["result"]["tracker"]["excursion"], json!(1));
    assert_eq!(second["tracker"]["state"], json!("active"));
    assert_eq!(second["tracker"]["active_excursion_ordinal"], json!(1));
    assert_eq!(second["tracker"]["next_excursion_ordinal"], json!(2));
    assert_eq!(
        second["tracker"]["updated_at"],
        json!("2026-01-05T12:10:00Z")
    );
}

// ---------------------------------------------------------------------------
// Evaluate node (auxiliary scopes: snooze conditions, sweep auto-resolve)
// ---------------------------------------------------------------------------

fn evaluate_node(request: &Value) -> Value {
    call_json(nocturne_alerts_evaluate_node, &request.to_string())
}

#[test]
fn evaluate_node_evaluates_a_tree_with_a_root_override() {
    let request = json!({
        "schema_version": 1,
        "rule_id": "00000000-0000-0000-0000-000000000001",
        "node": {
            "type": "composite",
            "composite": {
                "operator": "and",
                "conditions": [
                    { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                    { "type": "iob", "iob": { "operator": ">", "value": 1 } }
                ]
            }
        },
        "root": "snooze",
        "context": { "latest_value": 60, "latest_timestamp": "2026-01-05T12:00:00Z", "iob_units": 2 },
        "now": "2026-01-05T12:00:00Z",
    });
    let response = evaluate_node(&request);
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["value"], Value::Bool(true));
    assert_eq!(response["timers"], json!({}));
    assert_eq!(response["timer_ops"], json!([]));
}

#[test]
fn evaluate_node_threads_sustained_timers_under_the_root_override() {
    let rule_id = "00000000-0000-0000-0000-000000000002";
    let node = json!({
        "type": "sustained",
        "sustained": {
            "minutes": 10,
            "child": { "type": "threshold", "threshold": { "direction": "above", "value": 180 } }
        }
    });
    let context = json!({ "latest_value": 200, "latest_timestamp": "2026-01-05T12:00:00Z" });

    // First true sets the timer under the overridden root path and returns false.
    let first = evaluate_node(&json!({
        "schema_version": 1,
        "rule_id": rule_id,
        "node": node,
        "root": "auto_resolve",
        "context": context,
        "now": "2026-01-05T12:00:00Z",
    }));
    assert_eq!(first["ok"], Value::Bool(true));
    assert_eq!(first["value"], Value::Bool(false));
    assert_eq!(
        first["timers"]["auto_resolve"],
        json!("2026-01-05T12:00:00Z")
    );
    assert_eq!(
        first["timer_ops"],
        json!([{ "op": "set", "path": "auto_resolve", "at": "2026-01-05T12:00:00Z" }])
    );

    // Threading the persisted timer back in completes the window on schedule.
    let second = evaluate_node(&json!({
        "schema_version": 1,
        "rule_id": rule_id,
        "node": node,
        "root": "auto_resolve",
        "context": context,
        "now": "2026-01-05T12:10:00Z",
        "timers": first["timers"],
    }));
    assert_eq!(second["ok"], Value::Bool(true));
    assert_eq!(second["value"], Value::Bool(true));
    assert_eq!(second["timer_ops"], json!([]));
}

#[test]
fn evaluate_node_defaults_root_to_the_verbatim_type() {
    let request = json!({
        "schema_version": 1,
        "rule_id": "00000000-0000-0000-0000-000000000003",
        "node": {
            "type": "sustained",
            "sustained": {
                "minutes": 5,
                "child": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } }
            }
        },
        "context": { "latest_value": 60, "latest_timestamp": "2026-01-05T12:00:00Z" },
        "now": "2026-01-05T12:00:00Z",
    });
    let response = evaluate_node(&request);
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(
        response["timers"]["sustained"],
        json!("2026-01-05T12:00:00Z")
    );
}

#[test]
fn evaluate_node_unknown_kind_is_silent_false() {
    let request = json!({
        "schema_version": 1,
        "rule_id": "00000000-0000-0000-0000-000000000004",
        "node": { "type": "nope" },
        "root": "snooze",
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    });
    let response = evaluate_node(&request);
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["value"], Value::Bool(false));
}

#[test]
fn evaluate_node_rejects_malformed_node() {
    let request = json!({
        "schema_version": 1,
        "rule_id": "00000000-0000-0000-0000-000000000005",
        "node": "not an object",
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    });
    assert_error(&evaluate_node(&request), "malformed condition node");
}

#[test]
fn evaluate_node_rejects_null_pointer() {
    let response: Value = unsafe {
        let ptr = nocturne_alerts_evaluate_node(std::ptr::null());
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "null");
}

// ---------------------------------------------------------------------------
// Error envelopes
// ---------------------------------------------------------------------------

fn assert_error(response: &Value, fragment: &str) {
    assert_eq!(response["schema_version"], json!(1));
    assert_eq!(response["ok"], Value::Bool(false));
    let error = response["error"].as_str().expect("error message present");
    assert!(
        error.contains(fragment),
        "expected error containing '{fragment}', got '{error}'"
    );
}

#[test]
fn evaluate_rejects_null_pointer() {
    let response: Value = unsafe {
        let ptr = nocturne_alerts_evaluate(std::ptr::null());
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "null");
}

#[test]
fn leaf_paths_rejects_null_pointer() {
    let response: Value = unsafe {
        let ptr = nocturne_alerts_leaf_paths(std::ptr::null());
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "null");
}

#[test]
fn evaluate_rejects_invalid_utf8() {
    // 0xFF can never appear in UTF-8.
    let bytes = CString::new(vec![0xFFu8, 0x7B, 0x7D]).unwrap();
    let response: Value = unsafe {
        let ptr = nocturne_alerts_evaluate(bytes.as_ptr());
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "not valid UTF-8");
}

#[test]
fn evaluate_rejects_malformed_json() {
    assert_error(
        &call_json(nocturne_alerts_evaluate, "{ this is not json"),
        "invalid request envelope",
    );
}

#[test]
fn evaluate_rejects_wrong_schema_version() {
    let request = json!({
        "schema_version": 2,
        "rule": { "id": "00000000-0000-0000-0000-000000000001", "condition_type": "threshold" },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    });
    assert_error(&evaluate(&request), "unsupported schema_version 2");
}

#[test]
fn evaluate_rejects_unknown_condition_type() {
    let request = json!({
        "schema_version": 1,
        "rule": { "id": "00000000-0000-0000-0000-000000000001", "condition_type": "nope" },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    });
    assert_error(&evaluate(&request), "unknown condition_type 'nope'");
}

#[test]
fn evaluate_rejects_tracker_state_without_updated_at() {
    let request = json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-000000000001",
            "condition_type": "threshold",
            "condition_params": { "direction": "below", "value": 70 }
        },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
        "tracker": { "state": "active", "confirmation_count": 0, "next_excursion_ordinal": 2 },
    });
    assert_error(&evaluate(&request), "tracker.updated_at is required");
}

#[test]
fn boundary_converts_panics_to_error_envelopes() {
    let ptr = boundary(|| panic!("deliberate test panic"));
    let response: Value = unsafe {
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "panic in alert engine: deliberate test panic");
}

// ---------------------------------------------------------------------------
// Leaf paths
// ---------------------------------------------------------------------------

#[test]
fn leaf_paths_enumerates_nodes_and_leaves() {
    let node = json!({
        "type": "composite",
        "composite": {
            "operator": "and",
            "conditions": [
                {
                    "type": "sustained",
                    "sustained": {
                        "minutes": 10,
                        "child": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } }
                    }
                },
                { "type": "iob", "iob": { "operator": ">", "value": 1 } }
            ]
        }
    });
    let response = call_json(nocturne_alerts_leaf_paths, &node.to_string());
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["root"], json!("composite"));
    assert_eq!(
        response["paths"],
        json!([
            "composite",
            "composite[0].sustained",
            "composite[0].sustained[0].threshold",
            "composite[1].iob",
        ])
    );
    assert_eq!(
        response["leaves"],
        json!([
            { "leaf_id": 0, "path": "composite[0].sustained[0].threshold" },
            { "leaf_id": 1, "path": "composite[1].iob" },
        ])
    );
}

#[test]
fn leaf_paths_honours_root_override() {
    let input = json!({
        "root": "auto_resolve",
        "node": {
            "type": "sustained",
            "sustained": {
                "minutes": 5,
                "child": { "type": "threshold", "threshold": { "direction": "above", "value": 180 } }
            }
        }
    });
    let response = call_json(nocturne_alerts_leaf_paths, &input.to_string());
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["root"], json!("auto_resolve"));
    assert_eq!(
        response["paths"],
        json!(["auto_resolve", "auto_resolve[0].threshold"])
    );
    assert_eq!(
        response["leaves"],
        json!([{ "leaf_id": 0, "path": "auto_resolve[0].threshold" }])
    );
}

#[test]
fn leaf_paths_treats_container_with_missing_child_as_leaf() {
    // A sustained node without a child fails the container guard and IS a
    // leaf (LeafIdentity anomaly — normative).
    let node = json!({ "type": "sustained", "sustained": { "minutes": 10 } });
    let response = call_json(nocturne_alerts_leaf_paths, &node.to_string());
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["paths"], json!(["sustained"]));
    assert_eq!(
        response["leaves"],
        json!([{ "leaf_id": 0, "path": "sustained" }])
    );
}

#[test]
fn leaf_paths_rejects_non_object() {
    assert_error(
        &call_json(nocturne_alerts_leaf_paths, "[1, 2, 3]"),
        "must be a JSON object",
    );
}

#[test]
fn leaf_paths_rejects_malformed_node() {
    // `type` must be a string (or null/absent) — a number is a JsonException
    // in the C# engine.
    assert_error(
        &call_json(nocturne_alerts_leaf_paths, r#"{ "type": 5 }"#),
        "malformed condition node",
    );
}

// ---------------------------------------------------------------------------
// Classify (scoped Do Not Disturb scope class — ADR 0004)
// ---------------------------------------------------------------------------

fn classify(request: &Value) -> Value {
    call_json(nocturne_alerts_classify, &request.to_string())
}

fn classify_scope(condition_type: &str, condition_params: Value) -> String {
    let response = classify(&json!({
        "schema_version": 1,
        "condition_type": condition_type,
        "condition_params": condition_params,
    }));
    assert_eq!(response["schema_version"], json!(1));
    assert_eq!(
        response["ok"],
        Value::Bool(true),
        "error: {}",
        response["error"]
    );
    response["scope_class"]
        .as_str()
        .expect("scope_class present")
        .to_string()
}

#[test]
fn classify_threshold_below_is_low() {
    assert_eq!(
        classify_scope("threshold", json!({ "direction": "below", "value": 70 })),
        "low"
    );
}

#[test]
fn classify_threshold_above_is_high() {
    assert_eq!(
        classify_scope("threshold", json!({ "direction": "above", "value": 250 })),
        "high"
    );
}

#[test]
fn classify_composite_mixed_directions_is_composite() {
    // Children are full nodes (carry their own `type` + payload), exactly the
    // stored composite shape.
    let params = json!({
        "operator": "or",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
            { "type": "threshold", "threshold": { "direction": "above", "value": 250 } }
        ]
    });
    assert_eq!(classify_scope("composite", params), "composite");
}

#[test]
fn classify_signal_loss_is_undirected() {
    assert_eq!(
        classify_scope("signal_loss", json!({ "timeout_minutes": 20 })),
        "undirected"
    );
}

#[test]
fn classify_silent_fails_unknown_type_to_undirected() {
    // Unlike evaluate, an unknown condition_type is NOT an envelope error — the
    // crate's classify is the all-only safe default.
    assert_eq!(classify_scope("teleport", json!({})), "undirected");
}

#[test]
fn classify_defaults_missing_params_to_undirected() {
    let response = classify(&json!({
        "schema_version": 1,
        "condition_type": "threshold",
    }));
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["scope_class"], json!("undirected"));
}

#[test]
fn classify_rejects_null_pointer() {
    let response: Value = unsafe {
        let ptr = nocturne_alerts_classify(std::ptr::null());
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "null");
}

#[test]
fn classify_rejects_malformed_json() {
    assert_error(
        &call_json(nocturne_alerts_classify, "{ this is not json"),
        "invalid request envelope",
    );
}

#[test]
fn classify_rejects_wrong_schema_version() {
    let request = json!({
        "schema_version": 2,
        "condition_type": "threshold",
        "condition_params": { "direction": "below", "value": 70 },
    });
    assert_error(&classify(&request), "unsupported schema_version 2");
}

// ---------------------------------------------------------------------------
// Describe (condition readouts — ADR 0007): decode a rule's tree into a
// structured, leaf-id-tagged description. Static — no context, no truth.
// ---------------------------------------------------------------------------

fn describe(request: &Value) -> Value {
    call_json(nocturne_alerts_describe, &request.to_string())
}

fn describe_rule(condition_type: &str, condition_params: Value) -> Value {
    let response = describe(&json!({
        "schema_version": 1,
        "condition_type": condition_type,
        "condition_params": condition_params,
    }));
    assert_eq!(response["schema_version"], json!(1));
    assert_eq!(
        response["ok"],
        Value::Bool(true),
        "error: {}",
        response["error"]
    );
    response["tree"].clone()
}

#[test]
fn describe_threshold_leaf_carries_kind_and_operands() {
    let tree = describe_rule("threshold", json!({ "direction": "below", "value": 70 }));
    assert_eq!(tree["leaf_id"], json!(0));
    assert_eq!(tree["type"], json!("threshold"));
    assert_eq!(tree["kind"], json!("threshold"));
    assert_eq!(tree["params"]["direction"], json!("below"));
    // arbitrary_precision keeps the operand exact (no float round trip).
    assert_eq!(tree["params"]["value"], json!(70));
}

#[test]
fn describe_decimal_operand_is_exact() {
    // Build the params from a literal so the number keeps its exact form
    // (f64 would drop the trailing zero / round a long decimal); the operand
    // must survive describe verbatim.
    let params: Value = serde_json::from_str(r#"{ "operator": "<", "value": 1.250 }"#).unwrap();
    let tree = describe_rule("iob", params);
    assert_eq!(tree["kind"], json!("iob"));
    assert_eq!(tree["params"]["operator"], json!("<"));
    assert_eq!(tree["params"]["value"].to_string(), "1.250");
}

#[test]
fn describe_composite_preserves_structure_and_sustained_minutes() {
    let params = json!({
        "operator": "and",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 80 } },
            {
                "type": "sustained",
                "sustained": {
                    "minutes": 15,
                    "child": { "type": "iob", "iob": { "operator": "<", "value": 1 } }
                }
            }
        ]
    });
    let tree = describe_rule("composite", params);
    assert_eq!(tree["type"], json!("composite"));
    assert_eq!(tree["operator"], json!("and"));
    let conds = tree["conditions"].as_array().expect("conditions array");
    assert_eq!(conds.len(), 2);

    // Leaf 0: the bare threshold.
    assert_eq!(conds[0]["leaf_id"], json!(0));
    assert_eq!(conds[0]["kind"], json!("threshold"));

    // The sustained wrapper is a container (no leaf id) carrying its duration;
    // its child leaf takes id 1.
    assert_eq!(conds[1]["type"], json!("sustained"));
    assert_eq!(conds[1]["minutes"], json!(15));
    assert!(
        conds[1].get("leaf_id").is_none(),
        "sustained container has no leaf id"
    );
    assert_eq!(conds[1]["child"]["leaf_id"], json!(1));
    assert_eq!(conds[1]["child"]["kind"], json!("iob"));
}

#[test]
fn describe_emits_paths_that_match_timer_keys() {
    // Every node carries its canonical path; a sustained node's path equals the
    // key the engine stores its timer under (`condition_timers.path`), so the
    // host joins a duration node straight to its persisted first-true instant.
    let params = json!({
        "operator": "and",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 80 } },
            {
                "type": "sustained",
                "sustained": {
                    "minutes": 15,
                    "child": { "type": "iob", "iob": { "operator": "<", "value": 1 } }
                }
            }
        ]
    });
    let tree = describe_rule("composite", params);
    assert_eq!(tree["path"], json!("composite"));
    assert_eq!(
        tree["conditions"][0]["path"],
        json!("composite[0].threshold")
    );
    let sustained = &tree["conditions"][1];
    assert_eq!(sustained["path"], json!("composite[1].sustained"));
    assert_eq!(
        sustained["child"]["path"],
        json!("composite[1].sustained[0].iob")
    );
}

#[test]
fn describe_leaf_ids_align_with_evaluate_force_eval_log() {
    // The whole point of `describe`: its leaf ids must match `evaluate`'s
    // force-eval `leaves[]` so a host can join the two by `leaf_id`.
    let condition_params = json!({
        "operator": "and",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 80 } },
            {
                "type": "sustained",
                "sustained": {
                    "minutes": 15,
                    "child": { "type": "iob", "iob": { "operator": "<", "value": 1 } }
                }
            }
        ]
    });

    let described = describe_rule("composite", condition_params.clone());
    let mut described_ids = vec![
        described["conditions"][0]["leaf_id"].as_i64().unwrap(),
        described["conditions"][1]["child"]["leaf_id"]
            .as_i64()
            .unwrap(),
    ];
    described_ids.sort();

    let evaluated = evaluate(&json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-0000000000aa",
            "condition_type": "composite",
            "condition_params": condition_params,
        },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    }));
    assert_eq!(
        evaluated["ok"],
        Value::Bool(true),
        "error: {}",
        evaluated["error"]
    );
    let mut evaluated_ids: Vec<i64> = evaluated["result"]["leaves"]
        .as_array()
        .expect("leaves array")
        .iter()
        .map(|l| l["leaf_id"].as_i64().unwrap())
        .collect();
    evaluated_ids.sort();

    assert_eq!(described_ids, vec![0, 1]);
    assert_eq!(described_ids, evaluated_ids);
}

#[test]
fn describe_signal_loss_is_a_leaf_with_its_timeout() {
    // signal_loss has no leaves in evaluate (the rule is skipped), but the host
    // still needs to render its watchdog — describe exposes it as a leaf.
    let tree = describe_rule("signal_loss", json!({ "timeout_minutes": 20 }));
    assert_eq!(tree["leaf_id"], json!(0));
    assert_eq!(tree["kind"], json!("signal_loss"));
    assert_eq!(tree["params"]["timeout_minutes"], json!(20));
}

#[test]
fn describe_decodes_enum_ordinal_operands_to_names() {
    let tree = describe_rule("day_of_week", json!({ "days": [0, 6] }));
    assert_eq!(tree["kind"], json!("day_of_week"));
    assert_eq!(tree["params"]["days"], json!(["Sunday", "Saturday"]));
}

#[test]
fn describe_malformed_container_collapses_to_a_single_leaf() {
    // A composite whose `conditions` is not an array fails to parse: the engine
    // force-evaluates it as one (false) leaf, and describe matches — one leaf,
    // kind composite, no children.
    let tree = describe_rule(
        "composite",
        json!({ "operator": "and", "conditions": "nope" }),
    );
    assert_eq!(tree["leaf_id"], json!(0));
    assert_eq!(tree["kind"], json!("composite"));
    assert!(
        tree.get("conditions").is_none(),
        "malformed composite is a leaf, not a container"
    );
}

#[test]
fn describe_null_composite_slot_is_a_typeless_leaf() {
    let params = json!({
        "operator": "or",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
            null
        ]
    });
    let tree = describe_rule("composite", params);
    let conds = tree["conditions"].as_array().unwrap();
    assert_eq!(conds[0]["leaf_id"], json!(0));
    assert_eq!(conds[1]["leaf_id"], json!(1));
    assert_eq!(conds[1]["type"], Value::Null);
    assert_eq!(conds[1]["kind"], Value::Null);
}

#[test]
fn describe_rejects_unknown_condition_type() {
    assert_error(
        &describe(
            &json!({ "schema_version": 1, "condition_type": "teleport", "condition_params": {} }),
        ),
        "unknown condition_type 'teleport'",
    );
}

#[test]
fn describe_rejects_wrong_schema_version() {
    assert_error(
        &describe(&json!({ "schema_version": 2, "condition_type": "threshold" })),
        "unsupported schema_version 2",
    );
}

#[test]
fn describe_rejects_null_pointer() {
    let response: Value = unsafe {
        let ptr = nocturne_alerts_describe(std::ptr::null());
        let out = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        serde_json::from_str(&out).unwrap()
    };
    assert_error(&response, "null");
}

#[test]
fn describe_not_unwraps_to_its_child() {
    let tree = describe_rule(
        "not",
        json!({ "child": { "type": "threshold", "threshold": { "direction": "above", "value": 250 } } }),
    );
    assert_eq!(tree["type"], json!("not"));
    assert!(
        tree.get("leaf_id").is_none(),
        "not is a container, not a leaf"
    );
    assert_eq!(tree["child"]["leaf_id"], json!(0));
    assert_eq!(tree["child"]["kind"], json!("threshold"));
}

#[test]
fn describe_not_without_a_child_collapses_to_a_leaf() {
    let tree = describe_rule("not", json!({}));
    assert_eq!(tree["leaf_id"], json!(0));
    assert_eq!(tree["kind"], json!("not"));
}

#[test]
fn describe_noncanonical_typed_leaf_uses_default_operands_like_the_evaluator() {
    // A leaf whose `type` is the PascalCase enum-name (not the wire string) is
    // evaluated with constructor defaults (eval/mod.rs gate); describe must
    // surface the SAME defaults, not the authored operands, so the readout
    // agrees with the leaf's truth.
    let params = json!({
        "operator": "or",
        "conditions": [
            { "type": "RateOfChange", "rate_of_change": { "direction": "falling", "rate": 2 } }
        ]
    });
    let leaf = describe_rule("composite", params)["conditions"][0].clone();
    assert_eq!(leaf["kind"], json!("rate_of_change"));
    assert_eq!(
        leaf["params"]["direction"],
        Value::Null,
        "authored operand must be ignored"
    );
    assert_eq!(
        leaf["params"]["rate"],
        json!(0),
        "defaults surface, like the evaluator"
    );
}

#[test]
fn describe_canonical_typed_leaf_keeps_its_operands() {
    // Same operands under the canonical wire `type` ARE kept (contrast above).
    let params = json!({
        "operator": "or",
        "conditions": [
            { "type": "rate_of_change", "rate_of_change": { "direction": "falling", "rate": 2 } }
        ]
    });
    let leaf = describe_rule("composite", params)["conditions"][0].clone();
    assert_eq!(leaf["params"]["direction"], json!("falling"));
    assert_eq!(leaf["params"]["rate"], json!(2));
}

#[test]
fn describe_nested_composite_preorder_ids_align_with_evaluate() {
    let condition_params = json!({
        "operator": "and",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 80 } },
            {
                "type": "composite",
                "composite": {
                    "operator": "or",
                    "conditions": [
                        { "type": "iob", "iob": { "operator": "<", "value": 1 } },
                        { "type": "cob", "cob": { "operator": ">", "value": 20 } }
                    ]
                }
            }
        ]
    });
    let tree = describe_rule("composite", condition_params.clone());
    // Pre-order, depth-first: threshold = 0, then the nested composite's
    // iob = 1, cob = 2.
    assert_eq!(tree["conditions"][0]["leaf_id"], json!(0));
    let nested = &tree["conditions"][1];
    assert_eq!(nested["type"], json!("composite"));
    assert_eq!(nested["conditions"][0]["leaf_id"], json!(1));
    assert_eq!(nested["conditions"][1]["leaf_id"], json!(2));

    let evaluated = evaluate(&json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-0000000000bb",
            "condition_type": "composite",
            "condition_params": condition_params,
        },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    }));
    let ids: Vec<i64> = evaluated["result"]["leaves"]
        .as_array()
        .unwrap()
        .iter()
        .map(|l| l["leaf_id"].as_i64().unwrap())
        .collect();
    assert_eq!(ids, vec![0, 1, 2]);
}

#[test]
fn describe_decodes_remaining_enum_ordinal_operands() {
    assert_eq!(
        describe_rule(
            "temp_basal",
            json!({ "metric": 1, "operator": ">", "value": 150 })
        )["params"]["metric"],
        json!("percent_of_scheduled")
    );
    assert_eq!(
        describe_rule(
            "time_since_last_bolus",
            json!({ "operator": 2, "minutes": 30 })
        )["params"]["operator"],
        json!("<")
    );
    assert_eq!(
        describe_rule("glucose_bucket", json!({ "buckets": [0, 5] }))["params"]["buckets"],
        json!(["very_low", "very_high"])
    );
    assert_eq!(
        describe_rule("pump_state", json!({ "mode": 5, "is_active": true }))["params"]["mode"],
        json!("Sleep")
    );
    assert_eq!(
        describe_rule(
            "state_span_active",
            json!({ "category": 4, "is_active": true })
        )["params"]["category"],
        json!("Sleep")
    );
    // An out-of-range ordinal surfaces verbatim as a number (the engine accepts
    // raw integers).
    assert_eq!(
        describe_rule("pump_state", json!({ "mode": 99, "is_active": true }))["params"]["mode"],
        json!(99)
    );
}

#[test]
fn describe_alert_state_carries_uuid_and_null_for_minutes() {
    let tree = describe_rule(
        "alert_state",
        json!({ "alert_id": "00000000-0000-0000-0000-0000000000cc", "state": "firing" }),
    );
    assert_eq!(tree["kind"], json!("alert_state"));
    assert_eq!(
        tree["params"]["alert_id"],
        json!("00000000-0000-0000-0000-0000000000cc")
    );
    assert_eq!(tree["params"]["state"], json!("firing"));
    assert_eq!(tree["params"]["for_minutes"], Value::Null);
}

// ---------------------------------------------------------------------------
// UniFFI surface (feature-gated): the Kotlin-facing functions must expose the
// exact same envelope contract as the C ABI.
// ---------------------------------------------------------------------------

#[cfg(feature = "uniffi")]
mod uniffi_surface {
    use super::{assert_error, load_scenario, run_scenario, scenario_paths};
    use crate::uniffi_api;
    use serde_json::{Value, json};

    fn evaluate(request: &Value) -> Value {
        serde_json::from_str(&uniffi_api::evaluate(request.to_string()))
            .expect("uniffi evaluate returned valid JSON")
    }

    fn evaluate_node(request: &Value) -> Value {
        serde_json::from_str(&uniffi_api::evaluate_node(request.to_string()))
            .expect("uniffi evaluate_node returned valid JSON")
    }

    #[test]
    fn version_matches_crate_version() {
        assert_eq!(uniffi_api::version(), env!("CARGO_PKG_VERSION"));
    }

    #[test]
    fn corpus_scenario_round_trips_through_uniffi_surface() {
        // Two scenarios threaded tick-by-tick through the uniffi-exported
        // envelopes, pinned against the committed snapshots — same driver as
        // the C ABI corpus test. (The full corpus already runs through the C
        // ABI in this build; both paths share one envelope implementation.)
        //
        // A snooze scenario is included explicitly: it is the only shape that
        // reaches `evaluate_node`, and this test is the in-repo proxy for
        // prelude's Kotlin CorpusHarness, which drives exactly this surface.
        let paths = scenario_paths();
        let first = paths.first().expect("corpus is non-empty").clone();
        let snooze = paths
            .iter()
            .find(|p| {
                p.file_stem()
                    .is_some_and(|s| s.to_string_lossy().starts_with("snooze-"))
            })
            .expect("corpus has a snooze scenario")
            .clone();

        for path in [first, snooze] {
            let (scenario, expected) = load_scenario(&path);
            let actual = run_scenario(&scenario, evaluate, evaluate_node);
            assert_eq!(
                actual, expected,
                "scenario {}: uniffi output differs from expected snapshot",
                scenario.name
            );
        }
    }

    #[test]
    fn evaluate_node_shares_the_envelope_contract() {
        let response: Value = serde_json::from_str(&uniffi_api::evaluate_node(
            json!({
                "schema_version": 1,
                "rule_id": "00000000-0000-0000-0000-000000000001",
                "node": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                "root": "snooze",
                "context": { "latest_value": 60, "latest_timestamp": "2026-01-05T12:00:00Z" },
                "now": "2026-01-05T12:00:00Z",
            })
            .to_string(),
        ))
        .expect("valid JSON");
        assert_eq!(response["ok"], Value::Bool(true));
        assert_eq!(response["value"], Value::Bool(true));
    }

    #[test]
    fn leaf_paths_shares_the_envelope_contract() {
        let response: Value = serde_json::from_str(&uniffi_api::leaf_paths(
            json!({
                "root": "auto_resolve",
                "node": { "type": "threshold", "threshold": { "direction": "above", "value": 180 } }
            })
            .to_string(),
        ))
        .expect("valid JSON");
        assert_eq!(response["ok"], Value::Bool(true));
        assert_eq!(
            response["leaves"],
            json!([{ "leaf_id": 0, "path": "auto_resolve" }])
        );
    }

    #[test]
    fn describe_shares_the_envelope_contract() {
        let response: Value = serde_json::from_str(&uniffi_api::describe(
            json!({
                "schema_version": 1,
                "condition_type": "threshold",
                "condition_params": { "direction": "below", "value": 70 }
            })
            .to_string(),
        ))
        .expect("valid JSON");
        assert_eq!(response["ok"], Value::Bool(true));
        assert_eq!(response["tree"]["leaf_id"], json!(0));
        assert_eq!(response["tree"]["kind"], json!("threshold"));
    }

    #[test]
    fn errors_come_back_as_envelopes_not_exceptions() {
        let malformed: Value =
            serde_json::from_str(&uniffi_api::evaluate("{ nope".to_string())).expect("valid JSON");
        assert_error(&malformed, "invalid request envelope");

        let bad_schema = evaluate(&json!({
            "schema_version": 2,
            "rule": { "id": "00000000-0000-0000-0000-000000000001", "condition_type": "threshold" },
            "context": {},
            "now": "2026-01-05T12:00:00Z",
        }));
        assert_error(&bad_schema, "unsupported schema_version 2");
    }
}
