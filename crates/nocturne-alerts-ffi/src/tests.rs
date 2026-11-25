//! FFI-boundary tests: drive the C ABI functions directly (pointers in,
//! pointers out) — the full golden corpus threaded through the evaluate
//! envelope, plus the error-envelope contract (null, invalid UTF-8, malformed
//! JSON, bad schema, panic path).

#![allow(
    unsafe_code,
    clippy::undocumented_unsafe_blocks,
    reason = "tests call the C ABI"
)]

use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char};
use std::fs;
use std::path::PathBuf;

use serde_json::{Map, Value, json};

use crate::{
    envelope_string, nocturne_alerts_classify, nocturne_alerts_describe, nocturne_alerts_evaluate,
    nocturne_alerts_evaluate_node, nocturne_alerts_free_string, nocturne_alerts_leaf_paths,
    nocturne_alerts_references_any, nocturne_alerts_references_wall_clock,
    nocturne_alerts_rule_reads, nocturne_alerts_tzdb_version, nocturne_alerts_version,
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

pub(crate) fn call_json(
    f: unsafe extern "C" fn(*const c_char) -> *mut c_char,
    input: &str,
) -> Value {
    serde_json::from_str(&call(f, input)).expect("FFI returned valid JSON")
}

pub(crate) fn evaluate(request: &Value) -> Value {
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
fn the_leaf_log_is_produced_only_when_asked_for() {
    let mut request = json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-000000000001",
            "condition_type": "composite",
            "condition_params": { "operator": "and", "conditions": [
                { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
                { "type": "sustained", "sustained": { "minutes": 5, "child":
                    { "type": "iob", "iob": { "operator": "<", "value": 1 } } } }
            ] }
        },
        "context": { "latest_value": 60, "latest_timestamp": "2026-01-05T12:00:00Z", "iob_units": 0 },
        "now": "2026-01-05T12:00:00Z",
    });
    let by_default = evaluate(&request);
    request["include_leaves"] = json!(true);
    let mut with_leaves = evaluate(&request);
    assert_eq!(
        with_leaves["result"]["leaves"].as_array().map(Vec::len),
        Some(2)
    );
    request["include_leaves"] = json!(false);
    let without = evaluate(&request);
    with_leaves["result"]
        .as_object_mut()
        .unwrap()
        .remove("leaves");
    assert_eq!(without, with_leaves);
    assert_eq!(by_default, without, "the leaf log is off unless asked for");
}

#[test]
fn signal_loss_with_no_reading_ever_measures_from_the_rule_enablement() {
    let request = |enabled_at: Option<&str>, now: &str| {
        json!({
            "schema_version": 1,
            "rule": {
                "id": "00000000-0000-0000-0000-000000000001",
                "condition_type": "signal_loss",
                "condition_params": { "timeout_minutes": 20 },
                "enabled_at": enabled_at
            },
            "context": {},
            "now": now
        })
    };

    let waiting = evaluate(&request(
        Some("2026-01-05T12:00:00Z"),
        "2026-01-05T12:05:00Z",
    ));
    assert_eq!(waiting["ok"], json!(true), "{waiting}");
    assert_eq!(waiting["result"]["root"], json!(false));
    assert!(waiting["result"].get("unknown").is_none());
    assert_eq!(
        waiting["result"]["next_change_at"],
        json!("2026-01-05T12:20:00Z")
    );

    let lost = evaluate(&request(
        Some("2026-01-05T12:00:00Z"),
        "2026-01-05T12:20:00Z",
    ));
    assert_eq!(lost["result"]["root"], json!(true), "{lost}");
    assert_eq!(lost["result"]["transition"], json!("opened"));

    let unrecorded = evaluate(&request(None, "2026-01-05T12:20:00Z"));
    assert_eq!(unrecorded["result"]["unknown"], json!(true), "{unrecorded}");
    assert_eq!(
        unrecorded["result"]["next_change_at"],
        json!("2026-01-06T12:20:00Z"),
        "only the hold's bound"
    );

    assert_error(
        &evaluate(&request(Some("12:00"), "2026-01-05T12:20:00Z")),
        "invalid request envelope",
    );
}

/// A stored hold that an engine which does not record holds wrote over (an
/// active alert updated after `held_since`) restarts at the held evaluation
/// instead of reaching its stale bound (engine-semantics.md §6.4).
#[test]
fn evaluate_restarts_a_stored_hold_an_older_engine_wrote_over() {
    let response = evaluate(&json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-000000000001",
            "condition_type": "threshold",
            "condition_params": { "direction": "below", "value": 70 },
            "hysteresis_minutes": 15
        },
        "context": {},
        "now": "2026-01-08T12:00:00Z",
        "tracker": {
            "state": "active",
            "active_excursion_ordinal": 1,
            "updated_at": "2026-01-07T12:00:00Z",
            "held_since": "2026-01-05T12:00:00Z"
        }
    }));
    assert_eq!(response["ok"], json!(true), "{response}");
    assert_eq!(response["result"]["transition"], json!("none"));
    assert!(response["result"].get("held_too_long").is_none());
    assert_eq!(
        response["tracker"]["held_since"],
        json!("2026-01-08T12:00:00Z")
    );
    assert_eq!(response["tracker"]["state"], json!("active"));
}

#[test]
fn evaluate_reports_the_next_change_and_omits_none() {
    let request = |condition_type: &str, params: Value| {
        json!({
            "schema_version": 1,
            "rule": {
                "id": "00000000-0000-0000-0000-000000000001",
                "condition_type": condition_type,
                "condition_params": params
            },
            "context": {
                "latest_value": 120,
                "latest_timestamp": "2026-01-05T11:55:00Z",
                "last_reading_at": "2026-01-05T11:55:00Z",
                "iob_units": 1
            },
            "now": "2026-01-05T12:00:00Z"
        })
    };

    let wall_clock = evaluate(&request("signal_loss", json!({ "timeout_minutes": 15 })));
    assert_eq!(wall_clock["ok"], json!(true), "{wall_clock}");
    assert_eq!(
        wall_clock["result"]["next_change_at"],
        json!("2026-01-05T12:10:00Z")
    );

    let reading_driven = evaluate(&request(
        "threshold",
        json!({ "direction": "below", "value": 70 }),
    ));
    assert_eq!(reading_driven["ok"], json!(true), "{reading_driven}");
    assert_eq!(
        reading_driven["result"]["next_change_at"],
        json!("2026-01-05T12:06:00Z")
    );

    let constant = evaluate(&request("iob", json!({ "operator": ">", "value": 2 })));
    assert_eq!(constant["ok"], json!(true), "{constant}");
    assert!(constant["result"].get("next_change_at").is_none());
}

#[test]
fn tzdb_version_is_the_compiled_release() {
    unsafe {
        let ptr = nocturne_alerts_tzdb_version();
        let version = CStr::from_ptr(ptr).to_str().unwrap().to_string();
        nocturne_alerts_free_string(ptr);
        assert_eq!(version, nocturne_alerts_core::TZDB_VERSION);
        assert!(version.len() >= 5 && version.starts_with("20"), "{version}");
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
pub(crate) struct ScenarioFile {
    pub(crate) name: String,
    pub(crate) rules: Vec<Value>,
    pub(crate) ticks: Vec<ScenarioTick>,
}

#[derive(serde::Deserialize)]
pub(crate) struct ScenarioTick {
    pub(crate) at: String,
    pub(crate) context: Value,
}

fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/Parity/AlertEngineCorpus")
        .canonicalize()
        .expect("corpus directory exists")
}

/// Lists every corpus scenario file (excluding the `.expected.json`
/// snapshots), sorted for determinism.
pub(crate) fn scenario_paths() -> Vec<PathBuf> {
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

pub(crate) fn load_scenario(path: &PathBuf) -> (ScenarioFile, Value) {
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

/// Error prefix of a rule body that cannot be evaluated.
pub(crate) const UNEVALUABLE_RULE: &str = "malformed condition_params for ";

/// Drives one scenario through an evaluate-envelope function (C ABI or
/// UniFFI), threading the timers/tracker state envelopes between ticks exactly
/// as a host would, and returns the assembled expected-file Value with the
/// corpus's excursion numbering ([`number_as_corpus`]).
fn run_scenario(scenario: &ScenarioFile, evaluate: impl Fn(&Value) -> Value) -> Value {
    let mut timers: Map<String, Value> = Map::new(); // rule id -> timers object
    let mut not_true: Map<String, Value> = Map::new(); // rule id -> not_true object
    let mut trackers: Map<String, Value> = Map::new(); // rule id -> tracker object

    let ticks: Vec<Value> = scenario
        .ticks
        .iter()
        .map(|tick| {
            let rules: Vec<Value> = scenario
                .rules
                .iter()
                .map(|rule| {
                    let rule_id = rule["id"].as_str().expect("rule id").to_string();
                    let tracker = trackers.get(&rule_id).cloned().unwrap_or_else(|| json!({}));

                    let request = json!({
                        "schema_version": 1,
                        "rule": rule,
                        "context": tick.context,
                        "now": tick.at,
                        "timers": timers.get(&rule_id).cloned().unwrap_or_else(|| json!({})),
                        "not_true": not_true.get(&rule_id).cloned().unwrap_or_else(|| json!({})),
                        "tracker": tracker,
                        "include_leaves": true,
                    });

                    let response = evaluate(&request);
                    if response["ok"] == Value::Bool(false)
                        && response["error"]
                            .as_str()
                            .is_some_and(|e| e.starts_with(UNEVALUABLE_RULE))
                    {
                        // The host skips the rule and keeps its state.
                        return json!({ "rule_id": rule_id, "skipped": true });
                    }
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
                    not_true.insert(rule_id.clone(), response["not_true"].clone());
                    trackers.insert(rule_id.clone(), response["tracker"].clone());

                    response["result"].clone()
                })
                .collect();
            json!({ "at": tick.at, "rules": rules })
        })
        .collect();

    let mut actual = json!({
        "schema_version": 1,
        "scenario": scenario.name,
        "ticks": ticks,
    });
    number_as_corpus(&mut actual);
    actual
}

/// Renumbers each `tracker.excursion` as the corpus numbers excursions: 1, 2,
/// ... in the order they open across every rule of the scenario. The envelope
/// numbers each rule's own, so the corpus number is the rule and its ordinal
/// looked up in opening order.
pub(crate) fn number_as_corpus(actual: &mut Value) {
    let mut opened: HashMap<String, u64> = HashMap::new();
    let mut corpus: HashMap<(String, u64), u64> = HashMap::new();
    for tick in actual["ticks"].as_array_mut().expect("ticks") {
        for result in tick["rules"].as_array_mut().expect("rules") {
            let rule = result["rule_id"].as_str().expect("rule id").to_owned();
            if result["transition"] == "opened" {
                let ordinal = opened.entry(rule.clone()).or_default();
                *ordinal = ordinal.saturating_add(1);
                let next = u64::try_from(corpus.len())
                    .expect("a count")
                    .saturating_add(1);
                corpus.insert((rule.clone(), *ordinal), next);
            }
            if let Some(excursion) = result.pointer_mut("/tracker/excursion") {
                let ordinal = excursion.as_u64().expect("an ordinal");
                *excursion = json!(corpus[&(rule, ordinal)]);
            }
        }
    }
}

/// `actual` without the `next_change_at` of each result whose snapshot does
/// not carry one: a snapshot pins it by carrying it, and the corpus is frozen.
pub(crate) fn as_pinned(mut actual: Value, expected: &Value) -> Value {
    let expected_ticks = expected["ticks"].as_array().cloned().unwrap_or_default();
    if let Some(ticks) = actual["ticks"].as_array_mut() {
        for (tick, expected_tick) in ticks.iter_mut().zip(&expected_ticks) {
            let expected_rules = expected_tick["rules"]
                .as_array()
                .cloned()
                .unwrap_or_default();
            if let Some(rules) = tick["rules"].as_array_mut() {
                for (rule, expected_rule) in rules.iter_mut().zip(&expected_rules) {
                    if expected_rule.get("next_change_at").is_none()
                        && let Some(result) = rule.as_object_mut()
                    {
                        result.remove("next_change_at");
                    }
                }
            }
        }
    }
    actual
}

/// Runs the full corpus through an evaluate-envelope function and pins every
/// scenario against its committed `.expected.json` snapshot.
fn assert_corpus_round_trips(evaluate: impl Fn(&Value) -> Value) {
    let scenario_paths = scenario_paths();
    assert!(
        scenario_paths.len() >= 100,
        "expected >= 100 corpus scenarios, found {}",
        scenario_paths.len()
    );

    let mut failed: Vec<String> = Vec::new();
    for path in &scenario_paths {
        let (scenario, expected) = load_scenario(path);
        let actual = as_pinned(run_scenario(&scenario, &evaluate), &expected);
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
    assert_corpus_round_trips(evaluate);
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
    assert!(first["tracker"].get("last_excursion_ordinal").is_none());

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
    assert_eq!(second["tracker"]["last_excursion_ordinal"], json!(1));
    assert_eq!(
        second["tracker"]["updated_at"],
        json!("2026-01-05T12:10:00Z")
    );
}

fn hysteresis_request(now: &str, tracker: Value) -> Value {
    json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-00000000abce",
            "condition_type": "threshold",
            "condition_params": { "direction": "below", "value": 70 },
            "hysteresis_minutes": 30,
        },
        "context": { "latest_value": 100, "latest_timestamp": now },
        "now": now,
        "tracker": tracker,
    })
}

#[test]
fn hysteresis_start_round_trips_and_anchors_expiry() {
    let entered = evaluate(&hysteresis_request(
        "2026-01-05T12:05:00Z",
        json!({
            "state": "active",
            "active_excursion_ordinal": 1,
            "updated_at": "2026-01-05T12:00:00Z",
            "last_excursion_ordinal": 1,
        }),
    ));
    assert_eq!(entered["result"]["transition"], json!("hysteresis_started"));
    assert_eq!(
        entered["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:05:00Z")
    );
    assert_eq!(
        entered["result"]["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:05:00Z")
    );

    let held = evaluate(&hysteresis_request(
        "2026-01-05T12:30:00Z",
        entered["tracker"].clone(),
    ));
    assert_eq!(held["result"]["transition"], json!("none"));
    assert_eq!(
        held["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:05:00Z")
    );

    let closed = evaluate(&hysteresis_request(
        "2026-01-05T12:35:00Z",
        held["tracker"].clone(),
    ));
    assert_eq!(closed["result"]["transition"], json!("closed"));
    assert!(closed["tracker"].get("hysteresis_started_at").is_none());
}

#[test]
fn hysteresis_state_without_a_start_adopts_updated_at() {
    let response = evaluate(&hysteresis_request(
        "2026-01-05T12:30:00Z",
        json!({
            "state": "hysteresis",
            "active_excursion_ordinal": 1,
            "updated_at": "2026-01-05T12:10:00Z",
            "last_excursion_ordinal": 1,
        }),
    ));
    assert_eq!(response["ok"], Value::Bool(true));
    assert_eq!(response["result"]["transition"], json!("none"));
    assert_eq!(
        response["tracker"]["hysteresis_started_at"],
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
fn evaluate_node_reports_a_reading_dated_beyond_the_clock_tolerance() {
    let request = |reading: &str| {
        json!({
            "schema_version": 1,
            "rule_id": "00000000-0000-0000-0000-000000000001",
            "node": { "type": "signal_loss", "signal_loss": { "timeout_minutes": 20 } },
            "context": { "last_reading_at": reading },
            "now": "2026-01-05T12:00:00Z",
        })
    };
    let ahead = evaluate_node(&request("2026-01-05T13:00:00Z"));
    assert_eq!(ahead["value"], json!(false));
    assert_eq!(ahead["unknown"], json!(true));
    assert_eq!(
        ahead["clock_anomaly"],
        json!([{ "fact": "last_reading_at", "ahead_ms": 3_600_000 }])
    );

    let within = evaluate_node(&request("2026-01-05T12:05:00Z"));
    assert_eq!(within["value"], json!(false));
    assert!(within.get("unknown").is_none());
    assert!(within.get("clock_anomaly").is_none());
}

#[test]
fn evaluate_reports_a_reading_dated_ahead_and_holds_the_open_excursion() {
    let rule = json!({
        "id": "00000000-0000-0000-0000-000000000001",
        "condition_type": "signal_loss",
        "condition_params": { "timeout_minutes": 20 },
    });
    let response = call_json(
        nocturne_alerts_evaluate,
        &json!({
            "schema_version": 1,
            "rule": rule,
            "context": { "last_reading_at": "2026-01-05T13:00:00Z" },
            "now": "2026-01-05T12:00:00Z",
            "tracker": {
                "state": "active",
                "active_excursion_ordinal": 1,
                "updated_at": "2026-01-05T11:00:00Z",
                "last_excursion_ordinal": 1
            },
        })
        .to_string(),
    );
    assert_eq!(response["ok"], json!(true));
    let result = &response["result"];
    assert_eq!(result["unknown"], json!(true));
    assert_eq!(result["transition"], json!("none"));
    assert_eq!(response["tracker"]["state"], json!("active"));
    assert_eq!(
        result["clock_anomaly"],
        json!([{ "fact": "last_reading_at", "ahead_ms": 3_600_000 }])
    );
    assert_eq!(result["next_change_at"], json!("2026-01-05T12:55:00Z"));
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
fn evaluate_node_rejects_an_unknown_kind() {
    let request = json!({
        "schema_version": 1,
        "rule_id": "00000000-0000-0000-0000-000000000004",
        "node": { "type": "nope" },
        "root": "snooze",
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    });
    assert_error(
        &evaluate_node(&request),
        "malformed condition node: unknown_kind at 'snooze' (field 'type')",
    );
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

pub(crate) fn assert_error(response: &Value, fragment: &str) {
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
fn evaluate_reads_a_rules_glucose_freshness_bound() {
    let request = |minutes: Value| {
        json!({
            "schema_version": 1,
            "rule": {
                "id": "00000000-0000-0000-0000-000000000001",
                "condition_type": "threshold",
                "condition_params": { "direction": "above", "value": 180 },
                "glucose_freshness_minutes": minutes,
            },
            "context": {
                "latest_value": 200,
                "latest_timestamp": "2026-01-05T11:40:00Z",
                "last_reading_at": "2026-01-05T11:40:00Z"
            },
            "now": "2026-01-05T12:00:00Z",
        })
    };
    let default = evaluate(&request(Value::Null));
    assert_eq!(default["result"]["unknown"], json!(true), "{default}");
    let thirty = evaluate(&request(json!(30)));
    assert_eq!(thirty["result"]["root"], json!(true), "{thirty}");
    assert!(thirty["result"].get("unknown").is_none());
    for bad in [json!(0), json!(-1), json!(1441)] {
        assert_error(
            &evaluate(&request(bad)),
            "glucose_freshness_minutes must be a whole number of minutes from 1 to 1440",
        );
    }
}

#[test]
fn evaluate_rejects_an_on_unknown_word_naming_no_choice() {
    for word in ["Hold", "ignore", ""] {
        let request = json!({
            "schema_version": 1,
            "rule": {
                "id": "00000000-0000-0000-0000-000000000001",
                "condition_type": "threshold",
                "condition_params": { "direction": "above", "value": 180 },
                "on_unknown": word,
            },
            "context": {},
            "now": "2026-01-05T12:00:00Z",
        });
        assert_error(&evaluate(&request), &format!("unknown on_unknown '{word}'"));
    }
}

#[test]
fn evaluate_flags_an_unknown_root_and_leaf_beside_their_false_values() {
    let request = json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-000000000001",
            "condition_type": "not",
            "condition_params": {
                "child": { "type": "threshold", "threshold": { "direction": "above", "value": 180 } }
            },
        },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
        "include_leaves": true,
    });
    let result = &evaluate(&request)["result"];
    assert_eq!(result["root"], json!(false));
    assert_eq!(result["unknown"], json!(true));
    assert_eq!(
        result["leaves"],
        json!([{ "leaf_id": 0, "value": false, "unknown": true }])
    );
    assert_eq!(result["transition"], json!("none"));
}

#[test]
fn evaluate_node_flags_an_unknown_value() {
    let request = |context: Value| {
        call_json(
            crate::nocturne_alerts_evaluate_node,
            &json!({
                "schema_version": 1,
                "rule_id": "00000000-0000-0000-0000-000000000001",
                "node": { "type": "threshold", "threshold": { "direction": "above", "value": 180 } },
                "context": context,
                "now": "2026-01-05T12:00:00Z",
            })
            .to_string(),
        )
    };
    let unknown = request(json!({}));
    assert_eq!(unknown["value"], json!(false));
    assert_eq!(unknown["unknown"], json!(true));
    let known = request(json!({ "latest_value": 100 }));
    assert_eq!(known["value"], json!(false));
    assert_eq!(known.get("unknown"), None);
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
fn evaluate_rejects_structurally_malformed_condition_params() {
    // A wrong-shape payload must be rejected, so the host's per-rule catch
    // leaves timers and tracker untouched, rather than fail-closed-evaluated
    // (which would advance the tracker and silently close an active excursion).
    let request = json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-000000000001",
            "condition_type": "threshold",
            "condition_params": { "direction": 5, "value": 70 }
        },
        "context": {},
        "now": "2026-01-05T12:00:00Z",
    });
    assert_error(
        &evaluate(&request),
        "malformed condition_params for 'threshold'",
    );
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
        "tracker": { "state": "active", "last_excursion_ordinal": 1 },
    });
    assert_error(&evaluate(&request), "tracker.updated_at is required");
}

fn threshold_request_at(now: &str) -> Value {
    json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-000000000001",
            "condition_type": "threshold",
            "condition_params": { "direction": "below", "value": 70 }
        },
        "context": {},
        "now": now,
    })
}

#[test]
fn evaluate_rejects_now_outside_the_dotnet_range() {
    let response = evaluate(&threshold_request_at("+10000-01-01T00:00:00Z"));
    assert_error(&response, "now is outside the supported timestamp range");
    assert!(!response["error"].as_str().unwrap().contains("10000"));
}

#[test]
fn evaluate_rejects_timers_and_tracker_outside_the_dotnet_range() {
    let mut request = threshold_request_at("2026-01-05T12:00:00Z");
    request["timers"] = json!({ "threshold": "0000-06-01T00:00:00Z" });
    assert_error(&evaluate(&request), "timers is outside");

    let mut request = threshold_request_at("2026-01-05T12:00:00Z");
    request["tracker"] = json!({
        "state": "active",
        "updated_at": "0000-06-01T00:00:00Z",
        "last_excursion_ordinal": 1,
    });
    assert_error(&evaluate(&request), "tracker.updated_at is outside");
}

#[test]
fn evaluate_rejects_a_context_timestamp_outside_the_dotnet_range() {
    let mut request = threshold_request_at("2026-01-05T12:00:00Z");
    request["context"] = json!({ "last_carb_at": "0000-06-01T00:00:00Z" });
    assert_error(&evaluate(&request), "last_carb_at is outside");
}

#[test]
fn evaluate_node_rejects_now_outside_the_dotnet_range() {
    let request = json!({
        "schema_version": 1,
        "rule_id": "00000000-0000-0000-0000-000000000001",
        "node": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
        "context": {},
        "now": "0000-06-01T00:00:00Z",
    });
    assert_error(
        &evaluate_node(&request),
        "now is outside the supported timestamp range",
    );
}

#[test]
fn boundary_converts_panics_to_error_envelopes() {
    let response: Value =
        serde_json::from_str(&envelope_string(|| panic!("deliberate test panic"))).unwrap();
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
fn leaf_paths_rejects_a_node_that_cannot_be_evaluated() {
    let node = json!({ "type": "sustained", "sustained": { "minutes": 10 } });
    assert_error(
        &call_json(nocturne_alerts_leaf_paths, &node.to_string()),
        "malformed condition node: field_missing at 'sustained' (field 'child')",
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
    // `type` must be a string (or null/absent).
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

fn wall_clock(condition_type: &str, condition_params: Value) -> Value {
    call_json(
        nocturne_alerts_references_wall_clock,
        &json!({
            "schema_version": 1,
            "condition_type": condition_type,
            "condition_params": condition_params,
        })
        .to_string(),
    )
}

#[test]
fn references_wall_clock_selects_nested_wall_clock_leaves() {
    let nested = json!({
        "operator": "and",
        "conditions": [
            { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
            { "type": "staleness", "staleness": { "operator": ">", "value": 20 } },
        ]
    });
    let response = wall_clock("composite", nested);
    assert_eq!(response["ok"], json!(true));
    assert_eq!(response["references_wall_clock"], json!(true));
    assert_eq!(
        wall_clock("threshold", json!({ "direction": "below", "value": 70 }))["references_wall_clock"],
        json!(false)
    );
    assert_eq!(
        wall_clock("no_such_kind", json!({}))["references_wall_clock"],
        json!(false)
    );
}

#[test]
fn references_wall_clock_rejects_a_bad_envelope() {
    assert_error(
        &call_json(nocturne_alerts_references_wall_clock, "{ nope"),
        "invalid request envelope",
    );
    assert_error(
        &call_json(
            nocturne_alerts_references_wall_clock,
            r#"{"schema_version":2,"condition_type":"signal_loss"}"#,
        ),
        "unsupported schema_version 2",
    );
}

#[test]
fn references_wall_clock_reads_on_unknown_for_a_freshness_leaf() {
    let request = |on_unknown: Option<&str>| {
        let mut request = json!({
            "schema_version": 1,
            "condition_type": "threshold",
            "condition_params": { "direction": "above", "value": 180 },
        });
        if let Some(word) = on_unknown {
            request["on_unknown"] = word.into();
        }
        call_json(nocturne_alerts_references_wall_clock, &request.to_string())
    };
    assert_eq!(request(None)["references_wall_clock"], json!(false));
    assert_eq!(request(Some("hold"))["references_wall_clock"], json!(false));
    assert_eq!(request(Some("met"))["references_wall_clock"], json!(true));
    assert_eq!(
        request(Some("not_met"))["references_wall_clock"],
        json!(true)
    );
    assert_error(&request(Some("Hold")), "unknown on_unknown 'Hold'");
}

#[test]
fn references_wall_clock_walks_an_enabled_auto_resolve_tree() {
    let request = |enabled: bool| {
        json!({
            "schema_version": 1,
            "condition_type": "threshold",
            "condition_params": { "direction": "above", "value": 180 },
            "auto_resolve_enabled": enabled,
            "auto_resolve_params": { "type": "staleness", "staleness": { "operator": ">", "value": 30 } },
        })
        .to_string()
    };
    let enabled = call_json(nocturne_alerts_references_wall_clock, &request(true));
    assert_eq!(enabled["references_wall_clock"], json!(true));
    let disabled = call_json(nocturne_alerts_references_wall_clock, &request(false));
    assert_eq!(disabled["references_wall_clock"], json!(false));
}

fn high_rule_resolving_on_cob() -> Value {
    json!({
        "schema_version": 1,
        "condition_type": "threshold",
        "condition_params": { "direction": "above", "value": 180 },
        "auto_resolve_enabled": true,
        "auto_resolve_params": { "type": "cob", "cob": { "operator": "==", "value": 0 } },
    })
}

#[test]
fn rule_reads_names_facts_by_wire_name() {
    let response = call_json(
        nocturne_alerts_rule_reads,
        &high_rule_resolving_on_cob().to_string(),
    );
    assert_eq!(response["ok"], json!(true));
    assert_eq!(
        response["reads"],
        json!(["glucose", "carbs", "sensor_state"])
    );
    let unknown = call_json(
        nocturne_alerts_rule_reads,
        &json!({ "schema_version": 1, "condition_type": "no_such_kind" }).to_string(),
    );
    assert_eq!(unknown["reads"], json!([]));
}

#[test]
fn references_any_matches_facts_by_wire_name() {
    let with_facts = |facts: Value| {
        let mut request = high_rule_resolving_on_cob();
        request["facts"] = facts;
        call_json(nocturne_alerts_references_any, &request.to_string())
    };
    assert_eq!(with_facts(json!(["carbs"]))["references_any"], json!(true));
    assert_eq!(
        with_facts(json!(["insulin", "pump"]))["references_any"],
        json!(false)
    );
    assert_eq!(with_facts(json!([]))["references_any"], json!(false));
    assert_error(&with_facts(json!(["carbs", "cob"])), "unknown fact 'cob'");
    assert_error(
        &call_json(
            nocturne_alerts_references_any,
            &high_rule_resolving_on_cob().to_string(),
        ),
        "missing field `facts`",
    );
}

#[test]
fn rule_reads_and_references_any_reject_a_bad_envelope() {
    for export in [nocturne_alerts_rule_reads, nocturne_alerts_references_any] {
        assert_error(&call_json(export, "{ nope"), "invalid request envelope");
    }
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
        "include_leaves": true,
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
    let tree = describe_rule("signal_loss", json!({ "timeout_minutes": 20 }));
    assert_eq!(tree["leaf_id"], json!(0));
    assert_eq!(tree["kind"], json!("signal_loss"));
    assert_eq!(tree["params"]["timeout_minutes"], json!(20));
}

#[test]
fn describe_a_body_that_cannot_be_evaluated_is_null() {
    let low = json!({ "type": "threshold", "threshold": { "direction": "below", "value": 70 } });
    for (kind, params) in [
        (
            "composite",
            json!({ "operator": "or", "conditions": [low, null] }),
        ),
        ("not", json!({})),
        ("threshold", Value::Null),
        (
            "composite",
            json!({ "operator": "and", "conditions": "nope" }),
        ),
        ("pump_state", json!({ "mode": 5, "is_active": true })),
        ("threshold", json!({ "direction": "below", "value": "70" })),
    ] {
        assert_eq!(describe_rule(kind, params), Value::Null, "{kind}");
    }
}

#[test]
fn describe_without_auto_resolve_params_has_no_auto_resolve_tree() {
    let response = describe(&json!({
        "schema_version": 1,
        "condition_type": "threshold",
        "condition_params": { "direction": "below", "value": 70 },
    }));
    assert!(response.get("auto_resolve_tree").is_none());
}

#[test]
fn describe_auto_resolve_tree_is_rooted_at_auto_resolve() {
    let response = describe(&json!({
        "schema_version": 1,
        "condition_type": "threshold",
        "condition_params": { "direction": "below", "value": 70 },
        "auto_resolve_params": {
            "type": "composite",
            "composite": {
                "operator": "and",
                "conditions": [
                    { "type": "threshold", "threshold": { "direction": "above", "value": 90 } },
                    { "type": "iob", "iob": { "operator": "<", "value": 1 } }
                ]
            }
        },
    }));
    let tree = &response["auto_resolve_tree"];
    assert_eq!(tree["path"], json!("auto_resolve"));
    let conds = tree["conditions"].as_array().unwrap();
    assert_eq!(conds[0]["leaf_id"], json!(0));
    assert_eq!(conds[0]["path"], json!("auto_resolve[0].threshold"));
    assert_eq!(conds[1]["kind"], json!("iob"));
    assert_eq!(response["tree"]["leaf_id"], json!(0));
}

#[test]
fn describe_null_or_unevaluable_auto_resolve_tree_is_null() {
    for params in [
        Value::Null,
        json!({ "type": "threshold", "threshold": { "value": 90 } }),
    ] {
        let response = describe(&json!({
            "schema_version": 1,
            "condition_type": "threshold",
            "condition_params": { "direction": "below", "value": 70 },
            "auto_resolve_params": params,
        }));
        assert_eq!(response["ok"], json!(true));
        assert_eq!(response["auto_resolve_tree"], Value::Null, "for {params}");
    }
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
fn describe_leaf_keeps_its_operands() {
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
        "include_leaves": true,
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
fn describe_shows_enum_operands_by_wire_name() {
    let operand = |kind: &str, params: Value, field: &str| {
        describe_rule(kind, params)["params"][field].clone()
    };
    assert_eq!(
        operand(
            "day_of_week",
            json!({ "days": ["sunday", "saturday"] }),
            "days"
        ),
        json!(["sunday", "saturday"])
    );
    assert_eq!(
        operand(
            "temp_basal",
            json!({ "metric": "percent_of_scheduled", "operator": ">", "value": 150 }),
            "metric"
        ),
        json!("percent_of_scheduled")
    );
    assert_eq!(
        operand(
            "time_since_last_bolus",
            json!({ "operator": "<", "minutes": 30 }),
            "operator"
        ),
        json!("<")
    );
    assert_eq!(
        operand(
            "glucose_bucket",
            json!({ "buckets": ["very_low", "very_high"] }),
            "buckets"
        ),
        json!(["very_low", "very_high"])
    );
    assert_eq!(
        operand(
            "pump_state",
            json!({ "mode": "Sleep", "is_active": true }),
            "mode"
        ),
        json!("Sleep")
    );
    assert_eq!(
        operand(
            "state_span_active",
            json!({ "category": "Exercise", "is_active": true }),
            "category"
        ),
        json!("Exercise")
    );
    assert_eq!(
        operand(
            "time_of_day",
            json!({ "from": "09:00", "to": "17:00", "timezone": "Australia/Sydney" }),
            "timezone"
        ),
        json!("Australia/Sydney")
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
    use super::{as_pinned, assert_error, load_scenario, run_scenario, scenario_paths};
    use crate::uniffi_api;
    use serde_json::{Value, json};

    fn evaluate(request: &Value) -> Value {
        serde_json::from_str(&uniffi_api::evaluate(&request.to_string()))
            .expect("uniffi evaluate returned valid JSON")
    }

    #[test]
    fn version_matches_crate_version() {
        assert_eq!(uniffi_api::version(), env!("CARGO_PKG_VERSION"));
        assert_eq!(
            uniffi_api::tzdb_version(),
            nocturne_alerts_core::TZDB_VERSION
        );
    }

    #[test]
    fn classify_and_wall_clock_share_the_envelope_contract() {
        let request = json!({
            "schema_version": 1,
            "condition_type": "signal_loss",
            "condition_params": { "timeout_minutes": 15 }
        })
        .to_string();
        let classified: Value =
            serde_json::from_str(&uniffi_api::classify(&request)).expect("valid JSON");
        assert_eq!(classified["scope_class"], json!("undirected"));
        let wall_clock: Value =
            serde_json::from_str(&uniffi_api::references_wall_clock(&request)).expect("valid JSON");
        assert_eq!(wall_clock["ok"], json!(true));
        assert_eq!(wall_clock["references_wall_clock"], json!(true));
    }

    #[test]
    fn rule_reads_and_references_any_share_the_envelope_contract() {
        let mut request = json!({
            "schema_version": 1,
            "condition_type": "signal_loss",
            "condition_params": { "timeout_minutes": 15 },
            "facts": ["glucose"]
        });
        let reads: Value = serde_json::from_str(&uniffi_api::rule_reads(&request.to_string()))
            .expect("valid JSON");
        assert_eq!(reads["reads"], json!(["glucose"]));
        let any: Value = serde_json::from_str(&uniffi_api::references_any(&request.to_string()))
            .expect("valid JSON");
        assert_eq!(any["references_any"], json!(true));
        request["facts"] = json!(["nope"]);
        let unknown: Value =
            serde_json::from_str(&uniffi_api::references_any(&request.to_string()))
                .expect("valid JSON");
        assert_eq!(unknown["ok"], json!(false));
    }

    #[test]
    fn corpus_scenario_round_trips_through_uniffi_surface() {
        // One full scenario threaded tick-by-tick through the uniffi-exported
        // `evaluate`, pinned against the committed snapshot — same driver as
        // the C ABI corpus test. (The full corpus already runs through the C
        // ABI in this build; both paths share one envelope implementation.)
        let paths = scenario_paths();
        let (scenario, expected) = load_scenario(paths.first().expect("corpus is non-empty"));
        let actual = as_pinned(run_scenario(&scenario, evaluate), &expected);
        assert_eq!(
            actual, expected,
            "scenario {}: uniffi output differs from expected snapshot",
            scenario.name
        );
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
            .to_string()
            .as_str(),
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
            .to_string()
            .as_str(),
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
            .to_string()
            .as_str(),
        ))
        .expect("valid JSON");
        assert_eq!(response["ok"], Value::Bool(true));
        assert_eq!(response["tree"]["leaf_id"], json!(0));
        assert_eq!(response["tree"]["kind"], json!("threshold"));
    }

    #[test]
    fn errors_come_back_as_envelopes_not_exceptions() {
        let malformed: Value =
            serde_json::from_str(&uniffi_api::evaluate("{ nope")).expect("valid JSON");
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

/// A sustained{30} over an override that began at 11:00, evaluated at 12:00
/// from `state`, the request fields a host persisted.
fn override_held_since_eleven(state: Value) -> Value {
    let mut request = json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-00000000abcd",
            "condition_type": "sustained",
            "condition_params": {
                "minutes": 30,
                "child": { "type": "override_active", "override_active": { "is_active": true } }
            },
            "enabled_at": "2026-01-05T09:00:00Z",
        },
        "context": { "active_override": { "started_at": "2026-01-05T11:00:00Z" } },
        "now": "2026-01-05T12:00:00Z",
    });
    request
        .as_object_mut()
        .unwrap()
        .extend(state.as_object().unwrap().clone());
    evaluate(&request)
}

#[test]
fn not_true_round_trips_and_bounds_a_start() {
    let first = evaluate(&json!({
        "schema_version": 1,
        "rule": {
            "id": "00000000-0000-0000-0000-00000000abcd",
            "condition_type": "sustained",
            "condition_params": {
                "minutes": 30,
                "child": { "type": "override_active", "override_active": { "is_active": true } }
            },
        },
        "context": {},
        "now": "2026-01-05T11:45:00Z",
    }));
    assert_eq!(first["timers"], json!({}));
    assert_eq!(
        first["not_true"],
        json!({ "sustained": "2026-01-05T11:45:00Z" })
    );
    assert_eq!(
        first["result"]["timer_ops"],
        json!([{ "op": "clear", "path": "sustained", "at": "2026-01-05T11:45:00Z" }])
    );

    let second = override_held_since_eleven(json!({
        "timers": first["timers"],
        "not_true": first["not_true"],
        "tracker": first["tracker"],
    }));
    // The 11:45 record's lease ends at 11:50, where the run starts.
    assert_eq!(second["result"]["root"], json!(false));
    assert_eq!(
        second["timers"],
        json!({ "sustained": "2026-01-05T11:50:00Z" })
    );
    assert_eq!(second["not_true"], json!({}));
}

#[test]
fn a_rule_with_state_from_before_not_true_starts_its_run_at_now() {
    let tracker = json!({ "state": "idle", "updated_at": "2026-01-05T11:55:00Z" });
    let stored = override_held_since_eleven(json!({ "timers": {}, "tracker": tracker }));
    assert_eq!(stored["result"]["root"], json!(false));
    assert_eq!(
        stored["timers"],
        json!({ "sustained": "2026-01-05T12:00:00Z" })
    );

    // With no state at all it is the rule's first evaluation, bounded by its
    // enabled_at: the override has held 60 minutes since it began.
    let fresh = override_held_since_eleven(json!({}));
    assert_eq!(fresh["result"]["root"], json!(true));
    assert_eq!(
        fresh["timers"],
        json!({ "sustained": "2026-01-05T11:00:00Z" })
    );
}

#[test]
fn evaluate_rejects_not_true_outside_the_dotnet_range() {
    let response = override_held_since_eleven(json!({
        "not_true": { "sustained": "0000-06-01T00:00:00Z" },
    }));
    assert_error(&response, "not_true is outside");
}
