//! The replay entry point through the C ABI: the replay corpus round-trip and
//! the envelope errors.

use std::fs;
use std::path::PathBuf;

use serde_json::{Value, json};

use crate::nocturne_alerts_replay;
use crate::tests::{assert_error, call_json};

fn replay(request: &Value) -> Value {
    call_json(nocturne_alerts_replay, &request.to_string())
}

fn replay_corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/Parity/AlertEngineCorpus/replay")
        .canonicalize()
        .expect("replay corpus directory exists")
}

fn threshold_rule(id: &str) -> Value {
    json!({
        "id": id,
        "condition_type": "threshold",
        "condition_params": { "direction": "below", "value": 70 },
    })
}

fn tick(at: &str, mgdl: i64) -> Value {
    json!({
        "at": at,
        "context": { "latest_value": mgdl, "latest_timestamp": at, "last_reading_at": at },
    })
}

const RULE_1: &str = "00000000-0000-0000-0000-000000000001";

#[test]
fn replay_corpus_round_trips_through_the_c_abi() {
    let mut paths: Vec<PathBuf> = fs::read_dir(replay_corpus_dir())
        .expect("read replay corpus dir")
        .map(|e| e.expect("dir entry").path())
        .filter(|p| {
            p.extension().is_some_and(|ext| ext == "json")
                && !p.to_string_lossy().ends_with(".expected.json")
        })
        .collect();
    paths.sort();
    assert!(!paths.is_empty(), "no replay scenarios found");

    for path in paths {
        let scenario: Value =
            serde_json::from_str(&fs::read_to_string(&path).expect("read scenario"))
                .expect("parse scenario");
        let expected_path = path.with_extension("expected.json");
        let mut expected: Value =
            serde_json::from_str(&fs::read_to_string(&expected_path).expect("read expected"))
                .expect("parse expected");

        let mut response = replay(&json!({
            "schema_version": 1,
            "rules": scenario["rules"],
            "ticks": scenario["ticks"],
            "include_ticks": true,
        }));
        assert_eq!(
            response["ok"],
            json!(true),
            "{}: {}",
            path.display(),
            response["error"]
        );

        for (object, fields) in [
            (&mut response, ["schema_version", "ok"]),
            (&mut expected, ["schema_version", "scenario"]),
        ] {
            let map = object.as_object_mut().expect("object");
            for field in fields {
                map.remove(field);
            }
        }
        assert_eq!(response, expected, "{}", path.display());
    }
}

#[test]
fn the_tick_log_is_absent_unless_asked_for() {
    let response = replay(&json!({
        "schema_version": 1,
        "rules": [threshold_rule(RULE_1)],
        "ticks": [tick("2026-01-05T12:00:00Z", 60)],
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    assert!(response.get("ticks").is_none());
    assert_eq!(
        response["events"],
        json!([{ "at": "2026-01-05T12:00:00Z", "rule_id": RULE_1, "kind": "fired" }])
    );
}

#[test]
fn an_unevaluable_rule_is_skipped_not_an_error() {
    let response = replay(&json!({
        "schema_version": 1,
        "rules": [{
            "id": RULE_1,
            "condition_type": "threshold",
            "condition_params": { "direction": null, "value": 70 },
        }],
        "ticks": [tick("2026-01-05T12:00:00Z", 60)],
        "include_ticks": true,
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    assert_eq!(
        response["ticks"][0]["rules"],
        json!([{ "rule_id": RULE_1, "skipped": true }])
    );
}

#[test]
fn unusable_requests_are_error_envelopes() {
    let base = || {
        json!({
            "schema_version": 1,
            "rules": [threshold_rule(RULE_1)],
            "ticks": [tick("2026-01-05T12:00:00Z", 60)],
        })
    };

    let mut wrong_version = base();
    wrong_version["schema_version"] = json!(2);
    assert_error(&replay(&wrong_version), "unsupported schema_version 2");

    let mut unknown_kind = base();
    unknown_kind["rules"][0]["condition_type"] = json!("nonsense");
    assert_error(&replay(&unknown_kind), "unknown condition_type 'nonsense'");

    let mut duplicate = base();
    duplicate["rules"] = json!([threshold_rule(RULE_1), threshold_rule(RULE_1)]);
    assert_error(&replay(&duplicate), "appears more than once");

    let mut out_of_range = base();
    out_of_range["ticks"][0]["at"] = json!("0000-12-31T00:00:00Z");
    assert_error(&replay(&out_of_range), "ticks.at");

    assert_error(
        &replay(&json!({ "schema_version": 1 })),
        "invalid request envelope",
    );
}
