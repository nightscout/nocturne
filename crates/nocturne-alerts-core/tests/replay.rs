//! The replay driver (engine-semantics.md §8).

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use std::collections::HashSet;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::Rule;
use nocturne_alerts_core::model::ConditionKind;
use nocturne_alerts_core::replay::{
    ReplayError, ReplayEventKind, ReplayOptions, ReplayRuleState, ReplayTick, replay,
};

fn t(minutes: i64) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 12, 0, 0).unwrap() + TimeDelta::minutes(minutes)
}

fn id(n: u128) -> Uuid {
    Uuid::from_u128(n)
}

fn rule(n: u128, kind: ConditionKind, params: Value) -> Rule {
    Rule {
        id: id(n),
        condition_type: kind,
        condition_params: params,
        confirmation_readings: 1,
        hysteresis_minutes: 0,
        auto_resolve_enabled: false,
        auto_resolve_params: None,
    }
}

fn below(value: i32) -> Value {
    json!({ "direction": "below", "value": value })
}

fn firing_of(parent: u128) -> Value {
    json!({ "alert_id": id(parent).to_string(), "state": "firing" })
}

fn reading(minutes: i64, mgdl: i64) -> ReplayTick {
    ReplayTick {
        at: t(minutes),
        context: SensorContext {
            latest_value: Some(Decimal::from(mgdl)),
            latest_timestamp: Some(t(minutes)),
            last_reading_at: Some(t(minutes)),
            ..Default::default()
        },
        suppressed_rule_ids: HashSet::new(),
    }
}

fn with_ticks() -> ReplayOptions {
    ReplayOptions {
        include_ticks: true,
    }
}

fn kinds(
    events: &[nocturne_alerts_core::replay::ReplayEvent],
) -> Vec<(i64, Uuid, ReplayEventKind)> {
    events
        .iter()
        .map(|e| ((e.at - t(0)).num_minutes(), e.rule_id, e.kind))
        .collect()
}

#[test]
fn a_child_listed_first_runs_after_its_parent_and_sees_its_fire_on_the_same_tick() {
    let rules = [
        rule(2, ConditionKind::AlertState, firing_of(1)),
        rule(1, ConditionKind::Threshold, below(70)),
    ];
    let out = replay(&rules, [reading(0, 60)], with_ticks()).unwrap();

    assert_eq!(out.order, vec![id(1), id(2)]);
    assert_eq!(
        kinds(&out.events),
        vec![
            (0, id(1), ReplayEventKind::Fired),
            (0, id(2), ReplayEventKind::Fired)
        ]
    );
}

#[test]
fn a_cycle_keeps_the_given_order() {
    let rules = [
        rule(1, ConditionKind::AlertState, firing_of(2)),
        rule(2, ConditionKind::AlertState, firing_of(1)),
        rule(3, ConditionKind::Threshold, below(70)),
    ];
    let out = replay(&rules, [reading(0, 60)], ReplayOptions::default()).unwrap();
    assert_eq!(out.order, vec![id(1), id(2), id(3)]);
}

#[test]
fn a_duplicate_rule_id_is_an_error() {
    let rules = [
        rule(1, ConditionKind::Threshold, below(70)),
        rule(1, ConditionKind::Threshold, below(80)),
    ];
    let err = replay(&rules, [reading(0, 60)], ReplayOptions::default()).unwrap_err();
    assert_eq!(err, ReplayError::DuplicateRuleId(id(1)));
}

#[test]
fn a_continuous_fire_is_one_event_and_a_clear_resets_the_sustained_timer() {
    let sustained = json!({
        "minutes": 10,
        "child": { "type": "threshold", "threshold": below(70) }
    });
    let rules = [rule(1, ConditionKind::Sustained, sustained)];
    let ticks = [
        reading(0, 60),  // timer set
        reading(5, 60),  // 5 of 10
        reading(10, 60), // held: fires
        reading(15, 60), // continues
        reading(20, 90), // clears, timer reset
        reading(25, 60), // timer set again: not a fire
        reading(30, 60),
        reading(35, 60), // held again: fires
    ];
    let out = replay(&rules, ticks, with_ticks()).unwrap();

    assert_eq!(
        kinds(&out.events),
        vec![
            (10, id(1), ReplayEventKind::Fired),
            (20, id(1), ReplayEventKind::Cleared),
            (35, id(1), ReplayEventKind::Fired),
        ]
    );
}

#[test]
fn an_auto_resolve_true_on_the_opening_tick_fires_and_resolves_it_once_until_rearmed() {
    let mut r = rule(1, ConditionKind::Threshold, below(70));
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(json!({ "type": "threshold", "threshold": below(100) }));
    let ticks = [
        reading(0, 60),
        reading(5, 60),  // still met: awaits re-arm
        reading(10, 80), // body false: re-armed
        reading(15, 60),
    ];
    let out = replay(&[r], ticks, with_ticks()).unwrap();

    assert_eq!(
        kinds(&out.events),
        vec![
            (0, id(1), ReplayEventKind::Fired),
            (0, id(1), ReplayEventKind::AutoResolved),
            (15, id(1), ReplayEventKind::Fired),
            (15, id(1), ReplayEventKind::AutoResolved),
        ]
    );
    let ticks = out.ticks.unwrap();
    assert_eq!(
        ticks[0].rules[0].state,
        Some(ReplayRuleState {
            met: true,
            firing: false
        })
    );
}

#[test]
fn an_auto_resolved_rule_fires_again_when_its_resolve_tree_goes_false_while_its_body_holds() {
    let mut r = rule(1, ConditionKind::Threshold, below(70));
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(json!({
        "type": "sustained",
        "sustained": { "minutes": 10, "child": {
            "type": "threshold", "threshold": { "direction": "above", "value": 60 } } },
    }));
    let ticks = [
        reading(0, 65),
        reading(5, 65),
        reading(10, 65), // resolve held 10 minutes: auto-resolved
        reading(15, 65), // resolve still holds: nothing
        reading(20, 45), // resolve false, body true: fires
    ];
    let out = replay(&[r], ticks, with_ticks()).unwrap();

    assert_eq!(
        kinds(&out.events),
        vec![
            (0, id(1), ReplayEventKind::Fired),
            (10, id(1), ReplayEventKind::AutoResolved),
            (20, id(1), ReplayEventKind::Fired),
        ]
    );
}

#[test]
fn an_auto_resolve_that_keeps_holding_keeps_its_timer_and_never_fires_again() {
    let mut r = rule(1, ConditionKind::Threshold, below(70));
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(json!({
        "type": "sustained",
        "sustained": { "minutes": 10, "child": {
            "type": "threshold", "threshold": { "direction": "above", "value": 60 } } },
    }));
    let ticks = (0..=24).map(|n| reading(n * 5, 65));
    let out = replay(&[r], ticks, with_ticks()).unwrap();

    assert_eq!(
        kinds(&out.events),
        vec![
            (0, id(1), ReplayEventKind::Fired),
            (10, id(1), ReplayEventKind::AutoResolved),
        ]
    );
}

#[test]
fn an_unevaluable_body_is_skipped_on_every_tick() {
    let rules = [rule(
        1,
        ConditionKind::Threshold,
        json!({ "direction": null, "value": 70 }),
    )];
    let out = replay(&rules, [reading(0, 60)], with_ticks()).unwrap();

    assert!(out.events.is_empty());
    assert!(out.leaf_transitions.is_empty());
    assert_eq!(out.ticks.unwrap()[0].rules[0].state, None);
}

#[test]
fn a_fire_on_a_suppressed_tick_is_recorded_as_suppressed_and_still_fires_its_children() {
    let rules = [
        rule(1, ConditionKind::Threshold, below(70)),
        rule(2, ConditionKind::AlertState, firing_of(1)),
    ];
    let mut tick = reading(0, 60);
    tick.suppressed_rule_ids.insert(id(1));
    let out = replay(&rules, [tick], ReplayOptions::default()).unwrap();

    assert_eq!(
        kinds(&out.events),
        vec![
            (0, id(1), ReplayEventKind::SuppressedByDnd),
            (0, id(2), ReplayEventKind::Fired)
        ]
    );
}

#[test]
fn a_tick_with_no_reading_reads_as_fresh_and_a_gap_after_one_reads_as_stale() {
    let rules = [rule(
        1,
        ConditionKind::SignalLoss,
        json!({ "timeout_minutes": 15 }),
    )];
    let empty = |minutes| ReplayTick {
        at: t(minutes),
        context: SensorContext::default(),
        suppressed_rule_ids: HashSet::new(),
    };
    let stale = |minutes| ReplayTick {
        at: t(minutes),
        context: SensorContext {
            latest_value: Some(Decimal::from(100)),
            latest_timestamp: Some(t(20)),
            last_reading_at: Some(t(20)),
            ..Default::default()
        },
        suppressed_rule_ids: HashSet::new(),
    };
    let ticks = [empty(0), empty(15), reading(20, 100), stale(30), stale(35)];
    let out = replay(&rules, ticks, with_ticks()).unwrap();

    assert_eq!(
        kinds(&out.events),
        vec![(35, id(1), ReplayEventKind::Fired)]
    );
}

#[test]
fn the_leaf_log_records_the_first_observation_and_every_flip() {
    let composite = json!({
        "operator": "and",
        "conditions": [
            { "type": "threshold", "threshold": below(70) },
            { "type": "threshold", "threshold": below(200) }
        ]
    });
    let rules = [rule(1, ConditionKind::Composite, composite)];
    let out = replay(
        &rules,
        [
            reading(0, 100),
            reading(5, 60),
            reading(10, 60),
            reading(15, 100),
        ],
        ReplayOptions::default(),
    )
    .unwrap();

    let json = out.to_json();
    let ms = |m| t(m).timestamp_millis();
    assert_eq!(
        json["leaf_transitions"],
        json!([{
            "rule_id": id(1).to_string(),
            "leaves": [
                { "leaf_id": 0, "points": [
                    { "at_ms": ms(0), "value": false },
                    { "at_ms": ms(5), "value": true },
                    { "at_ms": ms(15), "value": false }
                ]},
                { "leaf_id": 1, "points": [{ "at_ms": ms(0), "value": true }] }
            ]
        }])
    );
}
