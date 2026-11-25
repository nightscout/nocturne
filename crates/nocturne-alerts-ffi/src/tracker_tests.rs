//! The tracker entry points through the C ABI: each transition, the envelope
//! errors, and equivalence with the tracker `evaluate` drives, across the
//! golden corpus.

use chrono::DateTime;
use nocturne_alerts_core::excursion::HOLD_BOUND;
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

fn process_at(tracker: &Value, met: bool, now: &str) -> Value {
    let response = process(&json!({
        "schema_version": 1,
        "tracker": tracker,
        "condition_met": met,
        "now": now,
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    response
}

#[test]
fn process_opens_continues_and_enters_hysteresis() {
    let opened = process_at(&Value::Null, true, "2026-01-05T12:00:00Z");
    assert_eq!(
        opened["transition"],
        json!({ "type": "opened", "excursion_ordinal": 1 })
    );
    assert_eq!(
        opened["tracker"],
        json!({
            "state": "active",
            "active_excursion_ordinal": 1,
            "updated_at": "2026-01-05T12:00:00Z",
            "last_excursion_ordinal": 1,
        })
    );

    let continues = process_at(&opened["tracker"], true, "2026-01-05T12:05:00Z");
    assert_eq!(
        continues["transition"],
        json!({ "type": "continues", "excursion_ordinal": 1 })
    );

    let entered = process_at(&continues["tracker"], false, "2026-01-05T12:10:00Z");
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
fn a_stored_confirming_state_reads_as_idle() {
    let stored = json!({
        "state": "confirming",
        "updated_at": "2026-01-05T11:55:00Z",
        "last_excursion_ordinal": 6,
    });

    let quiet = process_at(&stored, false, "2026-01-05T12:00:00Z");
    assert_eq!(quiet["transition"], json!({ "type": "none" }));
    assert_eq!(quiet["tracker"]["state"], json!("idle"));

    let fires = process_at(&stored, true, "2026-01-05T12:00:00Z");
    assert_eq!(
        fires["transition"],
        json!({ "type": "opened", "excursion_ordinal": 7 })
    );
    assert_eq!(fires["tracker"]["state"], json!("active"));
}

#[test]
fn force_close_reports_the_excursion_it_closed() {
    let opened = process_at(&Value::Null, true, "2026-01-05T12:00:00Z");
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
            "updated_at": "2026-01-05T12:03:00Z",
            "last_excursion_ordinal": 1,
        })
    );
}

fn process_resolving(tracker: &Value, met: bool, auto_resolve_met: bool, now: &str) -> Value {
    let response = process(&json!({
        "schema_version": 1,
        "tracker": tracker,
        "condition_met": met,
        "auto_resolve_met": auto_resolve_met,
        "now": now,
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    response
}

#[test]
fn an_auto_resolve_close_round_trips_awaiting_rearm_until_either_tree_is_false() {
    let opened = process_at(&Value::Null, true, "2026-01-05T12:00:00Z");
    let closed = force_close(&json!({
        "schema_version": 1,
        "tracker": opened["tracker"],
        "reason": "auto",
        "now": "2026-01-05T12:00:00Z",
    }));
    assert_eq!(closed["tracker"]["awaiting_rearm"], json!(true));

    let held = process_resolving(&closed["tracker"], true, true, "2026-01-05T12:00:30Z");
    assert_eq!(held["transition"], json!({ "type": "none" }));
    assert_eq!(held["tracker"]["awaiting_rearm"], json!(true));

    let rearmed = process_resolving(&held["tracker"], false, true, "2026-01-05T12:01:00Z");
    assert!(rearmed["tracker"].get("awaiting_rearm").is_none());
    let reopened = process_at(&rearmed["tracker"], true, "2026-01-05T12:01:30Z");
    assert_eq!(
        reopened["transition"],
        json!({ "type": "opened", "excursion_ordinal": 2 })
    );

    let relapse = process_resolving(&held["tracker"], true, false, "2026-01-05T12:01:00Z");
    assert_eq!(
        relapse["transition"],
        json!({ "type": "opened", "excursion_ordinal": 2 })
    );
    assert!(relapse["tracker"].get("awaiting_rearm").is_none());

    let absent = process_at(&held["tracker"], true, "2026-01-05T12:01:00Z");
    assert_eq!(absent["transition"]["type"], json!("opened"));
}

#[test]
fn an_unknown_stored_state_reads_as_active_with_an_excursion_and_idle_without() {
    let stored = |excursion: Option<u32>| {
        let mut tracker = json!({
            "state": "firing",
            "updated_at": "2026-01-05T11:55:00Z",
            "hysteresis_started_at": "2026-01-05T11:50:00Z",
            "awaiting_rearm": true,
            "last_excursion_ordinal": 3,
        });
        if let Some(ordinal) = excursion {
            tracker["active_excursion_ordinal"] = json!(ordinal);
        }
        tracker
    };

    let idle = process_at(&stored(None), true, "2026-01-05T12:00:00Z");
    assert_eq!(
        idle["transition"],
        json!({ "type": "opened", "excursion_ordinal": 4 })
    );

    let active = process_at(&stored(Some(3)), false, "2026-01-05T12:00:00Z");
    assert_eq!(
        active["transition"],
        json!({ "type": "hysteresis_started", "excursion_ordinal": 3 })
    );
    assert_eq!(
        active["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:00:00Z")
    );

    let closed = force_close(&json!({
        "schema_version": 1,
        "tracker": stored(Some(3)),
        "reason": "manual",
        "now": "2026-01-05T12:00:00Z",
    }));
    assert_eq!(
        closed["transition"],
        json!({ "type": "closed", "excursion_ordinal": 3, "close_reason": "manual" })
    );

    let swept = close_elapsed(&json!({
        "schema_version": 1,
        "tracker": stored(None),
        "now": "2026-01-05T12:00:00Z",
    }));
    assert_eq!(swept["transition"], json!({ "type": "none" }));
    assert_eq!(
        swept["tracker"],
        json!({
            "state": "idle",
            "updated_at": "2026-01-05T11:55:00Z",
            "last_excursion_ordinal": 3,
        })
    );
}

#[test]
fn force_close_without_an_excursion_changes_nothing() {
    let idle = process_at(&Value::Null, false, "2026-01-05T12:00:00Z");
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
    assert_eq!(never["tracker"], json!({}));
}

fn in_hysteresis(started_at: Option<&str>) -> Value {
    let mut tracker = json!({
        "state": "hysteresis",
        "active_excursion_ordinal": 4,
        "updated_at": "2026-01-05T12:10:00Z",
        "last_excursion_ordinal": 4,
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
        "now": now,
    }));
    assert_eq!(response["ok"], json!(true), "{}", response["error"]);
    response
}

/// A start beyond the tolerance ahead of `now` does not close; one within it
/// does (engine-semantics.md §5.4).
#[test]
fn close_elapsed_hysteresis_closes_at_the_window_end() {
    let tracker = in_hysteresis(Some("2026-01-05T12:40:00Z"));
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
            "updated_at": "2026-01-05T12:35:00Z",
            "last_excursion_ordinal": 4,
        })
    );
}

#[test]
fn close_elapsed_hysteresis_returns_the_adopted_start() {
    let held = close_elapsed_at(&in_hysteresis(None), "2026-01-05T12:04:00Z");
    assert_eq!(held["transition"], json!({ "type": "none" }));
    assert_eq!(
        held["tracker"]["hysteresis_started_at"],
        json!("2026-01-05T12:10:00Z")
    );
    let closed = close_elapsed_at(&held["tracker"], "2026-01-05T12:05:00Z");
    assert_eq!(closed["transition"]["type"], json!("closed"));
}

#[test]
fn close_elapsed_hysteresis_leaves_other_states_alone() {
    let active = process_at(&Value::Null, true, "2026-01-05T12:00:00Z");
    let response = close_elapsed_at(&active["tracker"], "2026-01-05T15:00:00Z");
    assert_eq!(response["transition"], json!({ "type": "none" }));
    assert_eq!(response["tracker"], active["tracker"]);
}

#[test]
fn tracker_entry_points_reject_unusable_requests() {
    let now = "2026-01-05T12:00:00Z";
    assert_error(
        &process(&json!({ "schema_version": 2, "condition_met": true, "now": now })),
        "unsupported schema_version 2",
    );
    assert_error(
        &process(&json!({ "schema_version": 1, "now": now })),
        "invalid request envelope",
    );
    assert_error(
        &process(&json!({
            "schema_version": 1,
            "tracker": { "state": "active" },
            "condition_met": true,
            "now": now,
        })),
        "tracker.updated_at is required",
    );
    assert_error(
        &force_close(&json!({ "schema_version": 1, "reason": "snoozed", "now": now })),
        "unknown close reason",
    );
    assert_error(
        &close_elapsed(&json!({
            "schema_version": 1,
            "now": "+10000-01-01T00:00:00Z",
        })),
        "now is outside the supported timestamp range",
    );
}

/// Whether the rule's enabled auto-resolve tree at the `auto_resolve` root,
/// against the timers before the tick, is anything but known false, which
/// is what keeps a re-arm hold; the body's timers sit under other roots, so
/// the order `evaluate` reads them in does not matter.
fn auto_resolve_not_false(
    rule: &Value,
    tick: &crate::tests::ScenarioTick,
    timers: Option<&Value>,
) -> bool {
    let node = &rule["auto_resolve_params"];
    if rule["auto_resolve_enabled"] != json!(true) || node.is_null() {
        return false;
    }
    let response = call_json(
        crate::nocturne_alerts_evaluate_node,
        &json!({
            "schema_version": 1,
            "rule_id": rule["id"],
            "node": node,
            "root": "auto_resolve",
            "context": tick.context,
            "now": tick.at,
            "timers": timers.cloned().unwrap_or_else(|| json!({})),
        })
        .to_string(),
    );
    response["ok"] == json!(true)
        && (response["value"] == json!(true) || response["unknown"] == json!(true))
}

/// Every corpus scenario, run through `evaluate` while a second tracker per
/// rule follows the same ticks through the tracker entry points: `process`
/// with the tick's root truth as `on_unknown` reads it (and, awaiting re-arm,
/// its auto-resolve truth), or `close_elapsed_hysteresis` for a root the rule
/// holds, then `force_close` when auto-resolve closed the excursion. The two
/// trackers and transitions must agree at every step. A held evaluation's
/// `held_since` is its own record, which no entry point makes, so the second
/// tracker takes it from `evaluate` before its close of an elapsed window, as
/// the engine records it first; from the hold's bound only `evaluate` decides
/// (§6.4), and the second tracker follows it.
#[test]
fn tracker_entry_points_follow_the_evaluate_tracker_across_the_corpus() {
    let paths = scenario_paths();
    assert!(paths.len() >= 100, "found {} corpus scenarios", paths.len());
    for path in &paths {
        let (scenario, _) = load_scenario(path);
        let mut timers: Map<String, Value> = Map::new();
        let mut via_evaluate: Map<String, Value> = Map::new();
        let mut via_entry_points: Map<String, Value> = Map::new();

        for tick in &scenario.ticks {
            for rule in &scenario.rules {
                let rule_id = rule["id"].as_str().expect("rule id").to_owned();
                let at = format!("scenario {} tick {} rule {rule_id}", scenario.name, tick.at);
                let tracker = via_evaluate
                    .get(&rule_id)
                    .cloned()
                    .unwrap_or_else(|| json!({}));
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

                let shadow = via_entry_points
                    .get(&rule_id)
                    .cloned()
                    .unwrap_or_else(|| json!({}));
                let held_since = response["tracker"].get("held_since").cloned();
                let past_bound = held_since
                    .as_ref()
                    .and_then(Value::as_str)
                    .and_then(|s| DateTime::parse_from_rfc3339(s).ok())
                    .zip(DateTime::parse_from_rfc3339(&tick.at).ok())
                    .is_some_and(|(since, now)| now - since >= HOLD_BOUND);
                if result.get("unknown").is_some() && past_bound {
                    timers.insert(rule_id.clone(), response["timers"].clone());
                    via_evaluate.insert(rule_id.clone(), response["tracker"].clone());
                    via_entry_points.insert(rule_id, response["tracker"].clone());
                    continue;
                }
                let auto_resolve_met = shadow["awaiting_rearm"] == json!(true)
                    && auto_resolve_not_false(rule, tick, timers.get(&rule_id));
                let condition_met = match (result.get("unknown"), rule.get("on_unknown")) {
                    (None, _) => Some(result["root"].clone()),
                    (Some(_), Some(word)) if word == "met" => Some(json!(true)),
                    (Some(_), Some(word)) if word == "not_met" => Some(json!(false)),
                    (Some(_), _) => None,
                };
                let mut shadow = shadow;
                let held_since_now = held_since.clone().filter(|_| condition_met.is_none());
                if condition_met.is_none()
                    && let (Some(since), Some(t)) = (held_since, shadow.as_object_mut())
                {
                    if t.is_empty() {
                        t.insert("state".into(), json!("idle"));
                        t.insert(
                            "updated_at".into(),
                            response["tracker"]["updated_at"].clone(),
                        );
                    }
                    t.insert("held_since".into(), since);
                }
                let processed = match condition_met {
                    Some(condition_met) => process(&json!({
                        "schema_version": 1,
                        "tracker": shadow,
                        "condition_met": condition_met,
                        "auto_resolve_met": auto_resolve_met,
                        "now": tick.at,
                    })),
                    None => close_elapsed(&json!({
                        "schema_version": 1,
                        "tracker": shadow,
                        "now": tick.at,
                    })),
                };
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
                // Restoring drops a hold the clock going back left stale; the
                // engine records the hold after it restores the state.
                if let (Some(since), Some(t)) = (held_since_now, after.as_object_mut()) {
                    t.insert("held_since".into(), since);
                }
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

                timers.insert(rule_id.clone(), response["timers"].clone());
                via_evaluate.insert(rule_id.clone(), response["tracker"].clone());
                via_entry_points.insert(rule_id, after);
            }
        }
    }
}
