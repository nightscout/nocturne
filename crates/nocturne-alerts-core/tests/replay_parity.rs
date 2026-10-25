//! Replay corpus parity: every scenario in
//! `tests/Parity/AlertEngineCorpus/replay/` through the replay driver must
//! match its committed `.expected.json` snapshot exactly. A new scenario gets
//! its snapshot from the engine under `NOCTURNE_CORPUS_BLESS=1`; a committed
//! snapshot is never rewritten.

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
use serde_json::{Map, Value};

use nocturne_alerts_core::engine::{Rule, WireRule};
use nocturne_alerts_core::replay::{ReplayOptions, ReplayTick, replay};

#[derive(Deserialize)]
struct ReplayScenarioFile {
    name: String,
    rules: Vec<WireRule>,
    ticks: Vec<ReplayTick>,
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
    let expected_path = path.with_extension("expected.json");
    let expected_text = fs::read_to_string(&expected_path).ok();

    let name = scenario.name.clone();
    let rules: Vec<Rule> = scenario
        .rules
        .into_iter()
        .map(|r| Rule::try_from(r).unwrap_or_else(|e| panic!("{e}")))
        .collect();
    let options = ReplayOptions {
        include_ticks: true,
    };
    let actual = replay(&rules, scenario.ticks, options)
        .expect("replay succeeds")
        .to_json();

    let Some(expected_text) = expected_text else {
        return bless(&expected_path, &name, actual);
    };
    let mut expected: Value = serde_json::from_str(&expected_text).expect("parse expected");
    let map = expected.as_object_mut().expect("expected is an object");
    map.remove("schema_version");
    map.remove("scenario");
    if actual == expected {
        Ok(())
    } else {
        Err(format!(
            "replay scenario {name}:\n  actual   = {actual}\n  expected = {expected}"
        ))
    }
}

/// Writes a new scenario's snapshot when `NOCTURNE_CORPUS_BLESS=1`, in the
/// committed files' shape (`schema_version` and `scenario` first).
fn bless(expected_path: &Path, name: &str, actual: Value) -> Result<(), String> {
    if std::env::var("NOCTURNE_CORPUS_BLESS").as_deref() != Ok("1") {
        return Err(format!(
            "{} is missing: run with NOCTURNE_CORPUS_BLESS=1 to write it from the engine, then review it",
            expected_path.display()
        ));
    }
    let mut snapshot = Map::new();
    snapshot.insert("schema_version".to_owned(), Value::from(1));
    snapshot.insert("scenario".to_owned(), Value::from(name));
    if let Value::Object(fields) = actual {
        snapshot.extend(fields);
    }
    let text =
        serde_json::to_string_pretty(&Value::Object(snapshot)).expect("serialise snapshot") + "\n";
    fs::write(expected_path, text)
        .unwrap_or_else(|e| panic!("write {}: {e}", expected_path.display()));
    Ok(())
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
