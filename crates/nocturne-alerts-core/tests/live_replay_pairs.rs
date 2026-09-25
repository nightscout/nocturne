//! Live and replay corpus scenarios that pin the same behaviour: a replay
//! scenario named `replay-<name>` beside a live scenario `<name>` must yield
//! the same events on the same ticks (engine-semantics.md §6.3, §8).

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    reason = "test code"
)]

use std::path::{Path, PathBuf};

use serde_json::Value;

fn corpus() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/Parity/AlertEngineCorpus")
}

fn read(path: &Path) -> Value {
    serde_json::from_str(&std::fs::read_to_string(path).expect("read scenario"))
        .expect("parse scenario")
}

/// The live snapshot as replay events. Replay has no confirmation or
/// hysteresis, so only a rule with neither is comparable: its clear is the
/// evaluation that enters hysteresis.
fn live_events(name: &str) -> Vec<(String, String, &'static str)> {
    let scenario = read(&corpus().join(format!("{name}.json")));
    for rule in scenario["rules"].as_array().unwrap() {
        let confirmation = rule["confirmation_readings"].as_i64().unwrap_or(1);
        let hysteresis = rule["hysteresis_minutes"].as_i64().unwrap_or(0);
        assert!(
            confirmation <= 1 && hysteresis == 0,
            "{name}: a paired live scenario needs confirmation 1 and hysteresis 0"
        );
    }
    let expected = read(&corpus().join(format!("{name}.expected.json")));
    let mut events = Vec::new();
    for tick in expected["ticks"].as_array().unwrap() {
        let at = tick["at"].as_str().unwrap().to_owned();
        for rule in tick["rules"].as_array().unwrap() {
            let id = rule["rule_id"].as_str().unwrap().to_owned();
            match rule["transition"].as_str() {
                Some("opened") => events.push((at.clone(), id.clone(), "fired")),
                Some("hysteresis_started") => events.push((at.clone(), id.clone(), "cleared")),
                _ => {}
            }
            if rule["auto_resolved"] == Value::Bool(true) {
                events.push((at.clone(), id, "auto_resolved"));
            }
        }
    }
    events
}

fn replay_events(path: &Path) -> Vec<(String, String, &'static str)> {
    read(path)["events"]
        .as_array()
        .unwrap()
        .iter()
        .map(|e| {
            let kind = match e["kind"].as_str().unwrap() {
                "fired" | "suppressed_by_dnd" => "fired",
                "auto_resolved" => "auto_resolved",
                "cleared" => "cleared",
                other => panic!("unknown replay event {other}"),
            };
            (
                e["at"].as_str().unwrap().to_owned(),
                e["rule_id"].as_str().unwrap().to_owned(),
                kind,
            )
        })
        .collect()
}

#[test]
fn paired_live_and_replay_scenarios_yield_the_same_events() {
    let mut pairs = 0;
    let mut cleared = false;
    for entry in std::fs::read_dir(corpus().join("replay")).unwrap() {
        let path = entry.unwrap().path();
        let file = path.file_name().unwrap().to_str().unwrap();
        let Some(name) = file
            .strip_suffix(".expected.json")
            .and_then(|n| n.strip_prefix("replay-"))
        else {
            continue;
        };
        if !corpus().join(format!("{name}.expected.json")).exists() {
            continue;
        }
        pairs += 1;
        assert_same_inputs(name);
        let live = live_events(name);
        cleared |= live.iter().any(|(_, _, kind)| *kind == "cleared");
        assert_eq!(
            live,
            replay_events(&path),
            "live {name} and replay-{name} diverge"
        );
    }
    assert!(
        pairs >= 2,
        "expected at least two paired scenarios, found {pairs}"
    );
    assert!(cleared, "no paired scenario exercises a clear");
}

/// A pair pins one behaviour only while both scenarios feed the same rules the
/// same readings at the same instants.
fn assert_same_inputs(name: &str) {
    let live = read(&corpus().join(format!("{name}.json")));
    let replay = read(&corpus().join("replay").join(format!("replay-{name}.json")));
    assert_eq!(
        live["rules"], replay["rules"],
        "{name}: paired rules differ"
    );
    let ticks = |s: &Value| -> Vec<(Value, Value)> {
        s["ticks"]
            .as_array()
            .unwrap()
            .iter()
            .map(|t| (t["at"].clone(), t["context"].clone()))
            .collect()
    };
    assert_eq!(ticks(&live), ticks(&replay), "{name}: paired ticks differ");
}
