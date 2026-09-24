//! The tracker entry points through the C ABI: each transition, the envelope
//! errors, and equivalence with the tracker `evaluate` drives, across the
//! golden corpus.

use serde_json::{Map, Value, json};

use crate::tests::{
    UNEVALUABLE_RULE, assert_error, call_json, evaluate, load_scenario, scenario_paths,
};
use crate::{
    nocturne_alerts_tracker_close_elapsed_hysteresis, nocturne_alerts_tracker_force_close,
    nocturne_alerts_tracker_process,
};

fn process(request: &Value) -> Value {
    call_json(nocturne_alerts_tracker_process, &request.to_string())
}

fn force_close(request: &Value) -> Value {
    call_json(nocturne_alerts_tracker_force_close, &request.to_string())
}

fn close_elapsed(request: &Value) -> Value {
    call_json(
        nocturne_alerts_tracker_close_elapsed_hysteresis,
        &request.to_string(),
    )
}

fn process_at(tracker: &Value, met: bool, now: &str, hysteresis_minutes: i32) -> Value {
    let response = process(&json!({
        "schema_version": 1,
        "tracker": tracker,
        "config": { "confirmation_readings": 1, "hysteresis_minutes": hysteresis_minutes },
        "condition_met": met,
        "now": now,
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    response
}

#[test]
fn process_opens_continues_and_enters_hysteresis() {
    let opened = process_at(&Value::Null, true, "2026-01-05T12:00:00Z", 30);
    assert_eq!(
        opened["transition"],
        json!({ "type": "opened", "excursion_ordinal": 1 })
    );
    assert_eq!(
        opened["tracker"],
        json!({
            "state": "active",
            "confirmation_count": 0,
            "active_excursion_ordinal": 1,
            "updated_at": "2026-01-05T12:00:00Z",
            "next_excursion_ordinal": 2,
        })
    );

    let continues = process_at(&opened["tracker"], true, "2026-01-05T12:05:00Z", 30);
    assert_eq!(
        continues["transition"],
        json!({ "type": "continues", "excursion_ordinal": 1 })
    );

    let entered = process_at(&continues["tracker"], false, "2026-01-05T12:10:00Z", 30);
    assert_eq!(
        entered["transition"],
        json!({ "type": "hysteresis_started", "excursion_ordinal": 1 })
    );
    assert_eq!(
        entered["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:10:00Z")
    );
}

#[test]
fn process_counts_confirmations_before_opening() {
    let request = |tracker: &Value, now: &str| {
        json!({
            "schema_version": 1,
            "tracker": tracker,
            "config": { "confirmation_readings": 2 },
            "condition_met": true,
            "now": now,
        })
    };
    let first = process(&request(
        &json!({ "next_excursion_ordinal": 7 }),
        "2026-01-05T12:00:00Z",
    ));
    assert_eq!(first["transition"], json!({ "type": "none" }));
    assert_eq!(first["tracker"]["state"], json!("confirming"));
    assert_eq!(first["tracker"]["confirmation_count"], json!(1));

    let second = process(&request(&first["tracker"], "2026-01-05T12:05:00Z"));
    assert_eq!(
        second["transition"],
        json!({ "type": "opened", "excursion_ordinal": 7 })
    );
    assert_eq!(second["tracker"]["next_excursion_ordinal"], json!(8));
}

#[test]
fn force_close_reports_the_excursion_it_closed() {
    let opened = process_at(&Value::Null, true, "2026-01-05T12:00:00Z", 0);
    let closed = force_close(&json!({
        "schema_version": 1,
        "tracker": opened["tracker"],
        "reason": "manual",
        "now": "2026-01-05T12:03:00Z",
    }));
    assert_eq!(closed["ok"], json!(true));
    assert_eq!(
        closed["transition"],
        json!({ "type": "closed", "excursion_ordinal": 1, "close_reason": "manual" })
    );
    assert_eq!(
        closed["tracker"],
        json!({
            "state": "idle",
            "confirmation_count": 0,
            "updated_at": "2026-01-05T12:03:00Z",
            "next_excursion_ordinal": 2,
        })
    );
}

#[test]
fn force_close_without_an_excursion_changes_nothing() {
    let idle = process_at(&Value::Null, false, "2026-01-05T12:00:00Z", 0);
    let response = force_close(&json!({
        "schema_version": 1,
        "tracker": idle["tracker"],
        "reason": "auto",
        "now": "2026-01-05T12:03:00Z",
    }));
    assert_eq!(response["transition"], json!({ "type": "none" }));
    assert_eq!(response["tracker"], idle["tracker"]);

    let never = force_close(&json!({
        "schema_version": 1,
        "reason": "auto",
        "now": "2026-01-05T12:03:00Z",
    }));
    assert_eq!(never["transition"], json!({ "type": "none" }));
    assert_eq!(never["tracker"], json!({ "next_excursion_ordinal": 1 }));
}

fn in_hysteresis(started_at: Option<&str>) -> Value {
    let mut tracker = json!({
        "state": "hysteresis",
        "confirmation_count": 0,
        "active_excursion_ordinal": 4,
        "updated_at": "2026-01-05T12:10:00Z",
        "next_excursion_ordinal": 5,
    });
    if let Some(at) = started_at {
        tracker["hysteresis_started_at"] = json!(at);
    }
    tracker
}

fn close_elapsed_at(tracker: &Value, now: &str) -> Value {
    let response = close_elapsed(&json!({
        "schema_version": 1,
        "tracker": tracker,
        "config": { "hysteresis_minutes": 30 },
        "now": now,
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    response
}

#[test]
fn close_elapsed_hysteresis_closes_at_the_window_end() {
    let tracker = in_hysteresis(Some("2026-01-05T12:05:00Z"));
    let held = close_elapsed_at(&tracker, "2026-01-05T12:34:59Z");
    assert_eq!(held["transition"], json!({ "type": "none" }));
    assert_eq!(held["tracker"], tracker);

    let closed = close_elapsed_at(&tracker, "2026-01-05T12:35:00Z");
    assert_eq!(
        closed["transition"],
        json!({ "type": "closed", "excursion_ordinal": 4, "close_reason": "hysteresis" })
    );
    assert_eq!(
        closed["tracker"],
        json!({
            "state": "idle",
            "confirmation_count": 0,
            "updated_at": "2026-01-05T12:35:00Z",
            "next_excursion_ordinal": 5,
        })
    );
}

#[test]
fn close_elapsed_hysteresis_returns_the_adopted_start() {
    let held = close_elapsed_at(&in_hysteresis(None), "2026-01-05T12:30:00Z");
    assert_eq!(held["transition"], json!({ "type": "none" }));
    assert_eq!(
        held["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:10:00Z")
    );
    let closed = close_elapsed_at(&held["tracker"], "2026-01-05T12:40:00Z");
    assert_eq!(closed["transition"]["type"], json!("closed"));
}

#[test]
fn close_elapsed_hysteresis_leaves_other_states_alone() {
    let active = process_at(&Value::Null, true, "2026-01-05T12:00:00Z", 0);
    let response = close_elapsed_at(&active["tracker"], "2026-01-05T15:00:00Z");
    assert_eq!(response["transition"], json!({ "type": "none" }));
    assert_eq!(response["tracker"], active["tracker"]);
}

#[test]
fn tracker_entry_points_reject_unusable_requests() {
    let now = "2026-01-05T12:00:00Z";
    assert_error(
        &process(&json!({ "schema_version": 2, "config": {}, "condition_met": true, "now": now })),
        "unsupported schema_version 2",
    );
    assert_error(
        &process(&json!({ "schema_version": 1, "condition_met": true, "now": now })),
        "invalid request envelope",
    );
    assert_error(
        &process(&json!({
            "schema_version": 1,
            "tracker": { "state": "active" },
            "config": {},
            "condition_met": true,
            "now": now,
        })),
        "tracker.updated_at is required",
    );
    assert_error(
        &process(&json!({
            "schema_version": 1,
            "tracker": { "state": "firing", "updated_at": now },
            "config": {},
            "condition_met": true,
            "now": now,
        })),
        "unknown tracker state",
    );
    assert_error(
        &force_close(&json!({ "schema_version": 1, "reason": "snoozed", "now": now })),
        "unknown close reason",
    );
    assert_error(
        &close_elapsed(&json!({
            "schema_version": 1,
            "config": {},
            "now": "+10000-01-01T00:00:00Z",
        })),
        "now is outside the supported timestamp range",
    );
}

fn tracker_config(rule: &Value) -> Value {
    json!({
        "confirmation_readings": rule.get("confirmation_readings").cloned().unwrap_or(json!(1)),
        "hysteresis_minutes": rule.get("hysteresis_minutes").cloned().unwrap_or(json!(0)),
    })
}

/// Every corpus scenario, run through `evaluate` while a second tracker per
/// rule follows the same ticks through the tracker entry points: `process`
/// with the tick's root truth, then `force_close` when auto-resolve closed
/// the excursion. The two trackers and transitions must agree at every step.
#[test]
fn tracker_entry_points_follow_the_evaluate_tracker_across_the_corpus() {
    let paths = scenario_paths();
    assert!(paths.len() >= 100, "found {} corpus scenarios", paths.len());
    for path in &paths {
        let (scenario, _) = load_scenario(path);
        let mut timers: Map<String, Value> = Map::new();
        let mut via_evaluate: Map<String, Value> = Map::new();
        let mut via_entry_points: Map<String, Value> = Map::new();
        let mut next_ordinal: u64 = 1;

        for tick in &scenario.ticks {
            for rule in &scenario.rules {
                let rule_id = rule["id"].as_str().expect("rule id").to_owned();
                let at = format!("scenario {} tick {} rule {rule_id}", scenario.name, tick.at);
                let mut tracker = via_evaluate
                    .get(&rule_id)
                    .cloned()
                    .unwrap_or_else(|| json!({}));
                tracker["next_excursion_ordinal"] = json!(next_ordinal);
                let response = evaluate(&json!({
                    "schema_version": 1,
                    "rule": rule,
                    "context": tick.context,
                    "now": tick.at,
                    "timers": timers.get(&rule_id).cloned().unwrap_or_else(|| json!({})),
                    "tracker": tracker,
                    "include_leaves": false,
                }));
                if response["error"]
                    .as_str()
                    .is_some_and(|e| e.starts_with(UNEVALUABLE_RULE))
                {
                    continue;
                }
                assert_eq!(response["ok"], json!(true), "{at}: {}", response["error"]);
                let result = &response["result"];

                let mut shadow = via_entry_points
                    .get(&rule_id)
                    .cloned()
                    .unwrap_or_else(|| json!({}));
                shadow["next_excursion_ordinal"] = json!(next_ordinal);
                let processed = process(&json!({
                    "schema_version": 1,
                    "tracker": shadow,
                    "config": tracker_config(rule),
                    "condition_met": result["root"],
                    "now": tick.at,
                }));
                assert_eq!(processed["ok"], json!(true), "{at}: {}", processed["error"]);
                assert_eq!(
                    processed["transition"]["type"], result["transition"],
                    "{at}"
                );
                assert_eq!(
                    processed["transition"].get("close_reason"),
                    result.get("close_reason"),
                    "{at}"
                );

                let mut after = processed["tracker"].clone();
                if result["auto_resolved"] == json!(true) {
                    let closed = force_close(&json!({
                        "schema_version": 1,
                        "tracker": after,
                        "reason": "auto",
                        "now": tick.at,
                    }));
                    assert_eq!(closed["transition"]["type"], json!("closed"), "{at}");
                    after = closed["tracker"].clone();
                }
                assert_eq!(after, response["tracker"], "{at}");

                next_ordinal = response["tracker"]["next_excursion_ordinal"]
                    .as_u64()
                    .expect("next_excursion_ordinal present");
                timers.insert(rule_id.clone(), response["timers"].clone());
                via_evaluate.insert(rule_id.clone(), response["tracker"].clone());
                via_entry_points.insert(rule_id, after);
            }
        }
    }
}
