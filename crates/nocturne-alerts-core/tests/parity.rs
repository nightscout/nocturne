//! Golden-corpus parity suite: runs every scenario in
//! `tests/Parity/AlertEngineCorpus/` through the Rust engine and asserts an
//! exact (semantic) match against the C#-generated `.expected.json` snapshot.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use std::collections::BTreeSet;
use std::fs;
use std::path::PathBuf;

use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Rule, RuleOutcome, evaluate_tick, format_instant};
use nocturne_alerts_core::model::ConditionKind;

// ---------------------------------------------------------------------------
// Scenario wire format (ScenarioModels.cs)
// ---------------------------------------------------------------------------

#[derive(Deserialize)]
struct ScenarioFile {
    name: String,
    rules: Vec<ScenarioRule>,
    ticks: Vec<ScenarioTick>,
}

fn default_confirmation_readings() -> i32 {
    1
}

#[derive(Deserialize)]
struct ScenarioRule {
    id: Uuid,
    condition_type: String,
    condition_params: Value,
    #[serde(default = "default_confirmation_readings")]
    confirmation_readings: i32,
    #[serde(default)]
    hysteresis_minutes: i32,
    #[serde(default)]
    auto_resolve_enabled: bool,
    #[serde(default)]
    auto_resolve_params: Option<Value>,
}

#[derive(Deserialize)]
struct ScenarioTick {
    at: DateTime<Utc>,
    context: SensorContext,
}

// ---------------------------------------------------------------------------
// Runner
// ---------------------------------------------------------------------------

fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/Parity/AlertEngineCorpus")
        .canonicalize()
        .expect("corpus directory exists")
}

fn run_scenario(scenario: &ScenarioFile) -> Value {
    let rules: Vec<Rule> = scenario
        .rules
        .iter()
        .map(|r| Rule {
            id: r.id,
            condition_type: ConditionKind::from_wire(&r.condition_type).unwrap_or_else(|| {
                panic!(
                    "scenario '{}': unknown condition_type '{}'",
                    scenario.name, r.condition_type
                )
            }),
            condition_params: r.condition_params.clone(),
            confirmation_readings: r.confirmation_readings,
            hysteresis_minutes: r.hysteresis_minutes,
            auto_resolve_enabled: r.auto_resolve_enabled,
            auto_resolve_params: r.auto_resolve_params.clone(),
        })
        .collect();

    let mut state = EngineState::new();
    let ticks: Vec<Value> = scenario
        .ticks
        .iter()
        .map(|tick| {
            let outcomes = evaluate_tick(&rules, &tick.context, tick.at, &mut state);
            json!({
                "at": format_instant(tick.at),
                "rules": outcomes.iter().map(RuleOutcome::to_json).collect::<Vec<_>>(),
            })
        })
        .collect();

    json!({
        "schema_version": 1,
        "scenario": scenario.name,
        "ticks": ticks,
    })
}

// ---------------------------------------------------------------------------
// Deep comparison with contextual diff messages
// ---------------------------------------------------------------------------

fn diff(actual: &Value, expected: &Value, path: &str, failures: &mut Vec<String>) {
    match (actual, expected) {
        (Value::Object(a), Value::Object(e)) => {
            let keys: BTreeSet<&String> = a.keys().chain(e.keys()).collect();
            for key in keys {
                let p = format!("{path}.{key}");
                match (a.get(key.as_str()), e.get(key.as_str())) {
                    (Some(av), Some(ev)) => diff(av, ev, &p, failures),
                    (Some(av), None) => {
                        failures.push(format!("{p}: unexpected field (actual = {av})"));
                    }
                    (None, Some(ev)) => {
                        failures.push(format!("{p}: missing field (expected = {ev})"));
                    }
                    (None, None) => unreachable!(),
                }
            }
        }
        (Value::Array(a), Value::Array(e)) => {
            if a.len() != e.len() {
                failures.push(format!(
                    "{path}: array length mismatch (actual = {}, expected = {})",
                    a.len(),
                    e.len()
                ));
                return;
            }
            for (i, (av, ev)) in a.iter().zip(e.iter()).enumerate() {
                diff(av, ev, &format!("{path}[{i}]"), failures);
            }
        }
        _ => {
            if actual != expected {
                failures.push(format!("{path}: actual = {actual}, expected = {expected}"));
            }
        }
    }
}

fn compare_scenario(name: &str, actual: &Value, expected: &Value) -> Vec<String> {
    let mut failures = Vec::new();
    let actual_ticks = actual["ticks"].as_array().expect("actual ticks");
    let expected_ticks = expected["ticks"].as_array().expect("expected ticks");
    if actual_ticks.len() != expected_ticks.len() {
        failures.push(format!(
            "scenario {name}: tick count mismatch (actual = {}, expected = {})",
            actual_ticks.len(),
            expected_ticks.len()
        ));
        return failures;
    }
    for (actual_tick, expected_tick) in actual_ticks.iter().zip(expected_ticks) {
        let at = expected_tick["at"].as_str().unwrap_or("?");
        let actual_rules = actual_tick["rules"].as_array().expect("actual rules");
        let expected_rules = expected_tick["rules"].as_array().expect("expected rules");
        if actual_tick["at"] != expected_tick["at"] {
            failures.push(format!(
                "scenario {name} tick {at}: at mismatch (actual = {})",
                actual_tick["at"]
            ));
        }
        if actual_rules.len() != expected_rules.len() {
            failures.push(format!(
                "scenario {name} tick {at}: rule count mismatch (actual = {}, expected = {})",
                actual_rules.len(),
                expected_rules.len()
            ));
            continue;
        }
        for (actual_rule, expected_rule) in actual_rules.iter().zip(expected_rules) {
            let rule_id = expected_rule["rule_id"].as_str().unwrap_or("?");
            diff(
                actual_rule,
                expected_rule,
                &format!("scenario {name} tick {at} rule {rule_id}"),
                &mut failures,
            );
        }
    }
    // Top-level envelope fields.
    for field in ["schema_version", "scenario"] {
        if actual[field] != expected[field] {
            failures.push(format!(
                "scenario {name} {field}: actual = {}, expected = {}",
                actual[field], expected[field]
            ));
        }
    }
    failures
}

#[test]
fn corpus_parity() {
    let dir = corpus_dir();
    let mut scenario_paths: Vec<PathBuf> = fs::read_dir(&dir)
        .expect("read corpus dir")
        .map(|e| e.expect("dir entry").path())
        .filter(|p| {
            p.extension().is_some_and(|ext| ext == "json")
                && !p
                    .file_name()
                    .is_some_and(|n| n.to_string_lossy().ends_with(".expected.json"))
        })
        .collect();
    scenario_paths.sort();

    assert!(
        scenario_paths.len() >= 100,
        "expected >= 100 corpus scenarios, found {} in {} — path bug?",
        scenario_paths.len(),
        dir.display()
    );

    let mut all_failures = Vec::new();
    let mut passed = 0usize;

    for path in &scenario_paths {
        let scenario_text = fs::read_to_string(path).expect("read scenario");
        let scenario: ScenarioFile = serde_json::from_str(&scenario_text)
            .unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));

        let expected_path = path.with_file_name(format!(
            "{}.expected.json",
            path.file_stem().unwrap().to_string_lossy()
        ));
        let expected_text = fs::read_to_string(&expected_path)
            .unwrap_or_else(|e| panic!("read {}: {e}", expected_path.display()));
        let expected: Value = serde_json::from_str(&expected_text).expect("parse expected");

        let actual = run_scenario(&scenario);
        let failures = compare_scenario(&scenario.name, &actual, &expected);
        if failures.is_empty() {
            passed += 1;
        } else {
            all_failures.extend(failures);
        }
    }

    assert!(
        all_failures.is_empty(),
        "{} of {} scenarios passed; {} differences:\n{}",
        passed,
        scenario_paths.len(),
        all_failures.len(),
        all_failures.join("\n")
    );
}
