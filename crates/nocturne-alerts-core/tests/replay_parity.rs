//! Replay corpus parity: every scenario in
//! `tests/Parity/AlertEngineCorpus/replay/` through the replay driver must
//! match its C#-generated `.expected.json` snapshot exactly.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    reason = "test code"
)]

use std::fs;
use std::path::{Path, PathBuf};

use serde::Deserialize;
use serde_json::Value;
use uuid::Uuid;

use nocturne_alerts_core::engine::Rule;
use nocturne_alerts_core::model::ConditionKind;
use nocturne_alerts_core::replay::{ReplayOptions, ReplayTick, replay};

#[derive(Deserialize)]
struct ReplayScenarioFile {
    name: String,
    rules: Vec<ScenarioRule>,
    ticks: Vec<ReplayTick>,
}

#[derive(Deserialize)]
struct ScenarioRule {
    id: Uuid,
    condition_type: String,
    condition_params: Value,
    #[serde(default)]
    auto_resolve_enabled: bool,
    #[serde(default)]
    auto_resolve_params: Option<Value>,
}

fn replay_corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/Parity/AlertEngineCorpus/replay")
        .canonicalize()
        .expect("replay corpus directory exists")
}

fn run(path: &Path) -> Result<(), String> {
    let scenario: ReplayScenarioFile =
        serde_json::from_str(&fs::read_to_string(path).expect("read scenario"))
            .unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
    let mut expected: Value = serde_json::from_str(
        &fs::read_to_string(path.with_extension("expected.json")).expect("read expected"),
    )
    .expect("parse expected");

    let rules: Vec<Rule> = scenario
        .rules
        .into_iter()
        .map(|r| Rule {
            id: r.id,
            condition_type: ConditionKind::from_wire(&r.condition_type)
                .unwrap_or_else(|| panic!("unknown condition_type '{}'", r.condition_type)),
            condition_params: r.condition_params,
            confirmation_readings: 1,
            hysteresis_minutes: 0,
            auto_resolve_enabled: r.auto_resolve_enabled,
            auto_resolve_params: r.auto_resolve_params,
        })
        .collect();
    let options = ReplayOptions {
        include_ticks: true,
    };
    let actual = replay(&rules, scenario.ticks, options)
        .expect("replay succeeds")
        .to_json();

    let map = expected.as_object_mut().expect("expected is an object");
    map.remove("schema_version");
    map.remove("scenario");
    if actual == expected {
        Ok(())
    } else {
        Err(format!(
            "replay scenario {}:\n  actual   = {actual}\n  expected = {expected}",
            scenario.name
        ))
    }
}

#[test]
fn replay_corpus_parity() {
    let mut paths: Vec<PathBuf> = fs::read_dir(replay_corpus_dir())
        .expect("read replay corpus dir")
        .map(|e| e.expect("dir entry").path())
        .filter(|p| {
            p.extension().is_some_and(|ext| ext == "json")
                && !p.to_string_lossy().ends_with(".expected.json")
        })
        .collect();
    paths.sort();
    assert!(
        paths.len() >= 9,
        "expected >= 9 replay scenarios, found {} — path bug?",
        paths.len()
    );

    let failures: Vec<String> = paths.iter().filter_map(|p| run(p).err()).collect();
    assert!(
        failures.is_empty(),
        "{} of {} replay scenarios differ:\n{}",
        failures.len(),
        paths.len(),
        failures.join("\n")
    );
}
