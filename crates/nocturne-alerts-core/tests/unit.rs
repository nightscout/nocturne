//! Representative port of the C# evaluator/tracker edge tests
//! (`ConditionEvaluatorTests.cs`, `ExcursionTrackerTests.cs`), covering null
//! permutations and state-machine edges the corpus may not pin individually.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::eval::{Env, eval_kind, eval_node};
use nocturne_alerts_core::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerStateKind, TransitionType,
};
use nocturne_alerts_core::model::{ConditionKind, Node, parse_payload, parse_payload_structure};
use nocturne_alerts_core::sustained::TimerStore;

fn base() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 12, 0, 0).unwrap()
}

fn d(s: &str) -> Decimal {
    s.parse().unwrap()
}

fn glucose_ctx(latest_value: Option<&str>, trend_rate: Option<&str>) -> SensorContext {
    SensorContext {
        latest_value: latest_value.map(d),
        latest_timestamp: Some(base()),
        trend_rate: trend_rate.map(d),
        last_reading_at: Some(base()),
        ..Default::default()
    }
}

fn eval_payload(kind: ConditionKind, payload: &Value, ctx: &SensorContext) -> bool {
    eval_payload_at(kind, payload, ctx, base())
}

fn eval_payload_at(
    kind: ConditionKind,
    payload: &Value,
    ctx: &SensorContext,
    now: DateTime<Utc>,
) -> bool {
    let parsed = parse_payload(kind, payload).expect("payload parses");
    let mut timers = TimerStore::new();
    let mut env = Env::new(now, Uuid::nil(), ctx, &mut timers);
    eval_kind(kind, Some(&parsed), kind.wire(), &mut env)
}

fn eval_tree(node_json: &Value, ctx: &SensorContext) -> bool {
    let node = Node::parse(node_json).expect("node parses");
    let mut timers = TimerStore::new();
    let mut env = Env::new(base(), Uuid::nil(), ctx, &mut timers);
    let root = node.type_str.clone().unwrap_or_default();
    eval_node(Some(&node), &root, &mut env)
}

// ---------------------------------------------------------------------------
// ThresholdEvaluator
// ---------------------------------------------------------------------------

#[test]
fn threshold_below_triggers_when_value_below_threshold() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(Some("65"), Some("0"));
    assert!(eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_below_does_not_trigger_when_value_above_threshold() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(Some("85"), Some("0"));
    assert!(!eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_below_exact_boundary_returns_false() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(Some("70"), Some("0"));
    assert!(!eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_above_triggers_when_value_above_threshold() {
    let payload = json!({"direction": "above", "value": 250});
    let ctx = glucose_ctx(Some("260"), Some("0"));
    assert!(eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_above_exact_boundary_returns_false() {
    let payload = json!({"direction": "above", "value": 250});
    let ctx = glucose_ctx(Some("250"), Some("0"));
    assert!(!eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_null_latest_value_returns_false() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(None, Some("0"));
    assert!(!eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_unknown_direction_returns_false() {
    let payload = json!({"direction": "sideways", "value": 70});
    let ctx = glucose_ctx(Some("50"), Some("0"));
    assert!(!eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

// ---------------------------------------------------------------------------
// RateOfChangeEvaluator
// ---------------------------------------------------------------------------

#[test]
fn rate_of_change_falling_triggers_at_negative_threshold() {
    // rate = -3.0, threshold = 3.0 => -3.0 <= -3.0 => true
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("-3.0"));
    assert!(eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

#[test]
fn rate_of_change_falling_triggers_below_negative_threshold() {
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("-4.0"));
    assert!(eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

#[test]
fn rate_of_change_falling_does_not_trigger_above_negative_threshold() {
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("-2.0"));
    assert!(!eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

#[test]
fn rate_of_change_rising_triggers_at_threshold() {
    let payload = json!({"direction": "rising", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("3.0"));
    assert!(eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

#[test]
fn rate_of_change_rising_does_not_trigger_below_threshold() {
    let payload = json!({"direction": "rising", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("2.0"));
    assert!(!eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

#[test]
fn rate_of_change_null_trend_rate_returns_false() {
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), None);
    assert!(!eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

#[test]
fn rate_of_change_unknown_direction_returns_false() {
    let payload = json!({"direction": "spinning", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("10"));
    assert!(!eval_payload(ConditionKind::RateOfChange, &payload, &ctx));
}

// ---------------------------------------------------------------------------
// CompositeEvaluator
// ---------------------------------------------------------------------------

fn composite_tree(operator: &str) -> Value {
    json!({
        "type": "composite",
        "composite": {
            "operator": operator,
            "conditions": [
                {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                {"type": "rate_of_change", "rate_of_change": {"direction": "falling", "rate": 3.0}}
            ]
        }
    })
}

#[test]
fn composite_and_all_true_returns_true() {
    let ctx = glucose_ctx(Some("60"), Some("-4.0"));
    assert!(eval_tree(&composite_tree("and"), &ctx));
}

#[test]
fn composite_and_one_false_returns_false() {
    let ctx = glucose_ctx(Some("60"), Some("-1.0"));
    assert!(!eval_tree(&composite_tree("and"), &ctx));
}

#[test]
fn composite_or_any_true_returns_true() {
    let ctx = glucose_ctx(Some("100"), Some("-4.0"));
    assert!(eval_tree(&composite_tree("or"), &ctx));
}

#[test]
fn composite_or_all_false_returns_false() {
    let ctx = glucose_ctx(Some("100"), Some("-1.0"));
    assert!(!eval_tree(&composite_tree("or"), &ctx));
}

#[test]
fn composite_empty_conditions_list_returns_false() {
    let tree = json!({"type": "composite", "composite": {"operator": "and", "conditions": []}});
    let ctx = glucose_ctx(Some("60"), Some("-4.0"));
    assert!(!eval_tree(&tree, &ctx));
}

#[test]
fn composite_unknown_operator_returns_false() {
    let tree = json!({
        "type": "composite",
        "composite": {
            "operator": "xor",
            "conditions": [
                {"type": "threshold", "threshold": {"direction": "below", "value": 70}}
            ]
        }
    });
    let ctx = glucose_ctx(Some("60"), Some("0"));
    assert!(!eval_tree(&tree, &ctx));
}

#[test]
fn composite_nested_works() {
    // Outer OR: (inner AND fails) OR (threshold above 250 succeeds).
    let tree = json!({
        "type": "composite",
        "composite": {
            "operator": "or",
            "conditions": [
                {"type": "composite", "composite": {
                    "operator": "and",
                    "conditions": [
                        {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
                        {"type": "rate_of_change", "rate_of_change": {"direction": "falling", "rate": 3.0}}
                    ]
                }},
                {"type": "threshold", "threshold": {"direction": "above", "value": 250}}
            ]
        }
    });
    let ctx = glucose_ctx(Some("300"), Some("-1.0"));
    assert!(eval_tree(&tree, &ctx));
}

// ---------------------------------------------------------------------------
// not / sustained edge cases
// ---------------------------------------------------------------------------

#[test]
fn not_missing_child_returns_false() {
    let tree = json!({"type": "not", "not": {}});
    let ctx = glucose_ctx(Some("100"), Some("0"));
    assert!(!eval_tree(&tree, &ctx));
}

#[test]
fn not_over_unknown_child_kind_returns_true() {
    // Child of unknown kind evaluates false; not inverts to true. [normative]
    let tree = json!({"type": "not", "not": {"child": {"type": "warp_drive"}}});
    let ctx = glucose_ctx(Some("100"), Some("0"));
    assert!(eval_tree(&tree, &ctx));
}

#[test]
fn sustained_zero_minutes_never_fires() {
    let tree = json!({
        "type": "sustained",
        "sustained": {
            "minutes": 0,
            "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}
        }
    });
    let ctx = glucose_ctx(Some("60"), Some("0"));
    assert!(!eval_tree(&tree, &ctx));
}

#[test]
fn sustained_missing_child_returns_false() {
    let tree = json!({"type": "sustained", "sustained": {"minutes": 5}});
    let ctx = glucose_ctx(Some("60"), Some("0"));
    assert!(!eval_tree(&tree, &ctx));
}

// ---------------------------------------------------------------------------
// staleness / time_since cold-start conventions
// ---------------------------------------------------------------------------

#[test]
fn staleness_infinity_convention_with_reading_history() {
    // last_reading_at null but latest_timestamp present: elapsed = +infinity.
    let ctx = SensorContext {
        latest_value: Some(d("100")),
        latest_timestamp: Some(base()),
        last_reading_at: None,
        ..Default::default()
    };
    assert!(eval_payload(
        ConditionKind::Staleness,
        &json!({"operator": ">", "value": 15}),
        &ctx
    ));
    assert!(eval_payload(
        ConditionKind::Staleness,
        &json!({"operator": ">=", "value": 15}),
        &ctx
    ));
    assert!(!eval_payload(
        ConditionKind::Staleness,
        &json!({"operator": "<", "value": 15}),
        &ctx
    ));
    assert!(!eval_payload(
        ConditionKind::Staleness,
        &json!({"operator": "==", "value": 15}),
        &ctx
    ));
}

#[test]
fn staleness_cold_start_short_circuits_before_infinity() {
    let ctx = SensorContext::default();
    assert!(!eval_payload(
        ConditionKind::Staleness,
        &json!({"operator": ">", "value": 15}),
        &ctx
    ));
}

#[test]
fn time_since_last_carb_missing_anchor_is_positive_infinity() {
    let ctx = SensorContext::default();
    assert!(eval_payload(
        ConditionKind::TimeSinceLastCarb,
        &json!({"operator": ">", "minutes": 30}),
        &ctx
    ));
    assert!(eval_payload(
        ConditionKind::TimeSinceLastCarb,
        &json!({"operator": ">=", "minutes": 30}),
        &ctx
    ));
    assert!(!eval_payload(
        ConditionKind::TimeSinceLastCarb,
        &json!({"operator": "<", "minutes": 30}),
        &ctx
    ));
    assert!(!eval_payload(
        ConditionKind::TimeSinceLastCarb,
        &json!({"operator": "==", "minutes": 30}),
        &ctx
    ));
}

#[test]
fn temp_basal_percent_with_zero_scheduled_rate_returns_false() {
    let ctx = SensorContext {
        active_temp_basal: Some(nocturne_alerts_core::context::TempBasalSnapshot {
            rate: d("1.5"),
            scheduled_rate: Some(Decimal::ZERO),
            started_at: base(),
        }),
        ..Default::default()
    };
    assert!(!eval_payload(
        ConditionKind::TempBasal,
        &json!({"metric": "percent_of_scheduled", "operator": ">=", "value": 50}),
        &ctx
    ));
}

#[test]
fn state_span_active_pump_mode_category_is_always_false() {
    let ctx = SensorContext::default();
    assert!(!eval_payload(
        ConditionKind::StateSpanActive,
        &json!({"category": "PumpMode", "state": null, "is_active": false}),
        &ctx
    ));
}

#[test]
fn sleep_session_active_matches_the_asserted_side_of_the_signal() {
    let active = SensorContext {
        sleep_session_active: true,
        ..Default::default()
    };
    assert!(eval_payload(
        ConditionKind::SleepSessionActive,
        &json!({"is_active": true}),
        &active
    ));
    assert!(!eval_payload(
        ConditionKind::SleepSessionActive,
        &json!({"is_active": false}),
        &active
    ));

    let idle = SensorContext::default();
    assert!(eval_payload(
        ConditionKind::SleepSessionActive,
        &json!({"is_active": false}),
        &idle
    ));
    assert!(!eval_payload(
        ConditionKind::SleepSessionActive,
        &json!({"is_active": true}),
        &idle
    ));
}

#[test]
fn loop_stale_null_timestamp_with_guard_set_returns_false() {
    // Guard passes but the cycle timestamp is null: no infinity convention.
    let ctx = SensorContext {
        has_ever_aps_cycled: true,
        last_aps_cycle_at: None,
        ..Default::default()
    };
    assert!(!eval_payload(
        ConditionKind::LoopStale,
        &json!({"operator": ">", "minutes": 15}),
        &ctx
    ));
}

// ---------------------------------------------------------------------------
// ExcursionTracker
// ---------------------------------------------------------------------------

fn rule_id() -> Uuid {
    Uuid::from_u128(1)
}

fn cfg(confirmation_readings: i32, hysteresis_minutes: i32) -> TrackerRuleConfig {
    TrackerRuleConfig {
        confirmation_readings,
        hysteresis_minutes,
    }
}

fn at(minutes: i64) -> DateTime<Utc> {
    base() + TimeDelta::minutes(minutes)
}

#[test]
fn tracker_idle_false_evaluation_stays_idle() {
    let mut tracker = ExcursionTracker::new();
    let t = tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(0));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn tracker_idle_true_with_confirmation_greater_than_1_transitions_to_confirming() {
    let mut tracker = ExcursionTracker::new();
    let t = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    assert_eq!(t.kind, TransitionType::None);
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Confirming);
    assert_eq!(state.confirmation_count, 1);
}

#[test]
fn tracker_idle_true_with_confirmation_1_goes_directly_to_active() {
    let mut tracker = ExcursionTracker::new();
    let t = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(t.excursion, Some(1));
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Active);
    assert_eq!(state.confirmation_count, 0);
}

#[test]
fn tracker_confirming_false_evaluation_resets_to_idle() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(3, 0), false, at(5));
    assert_eq!(t.kind, TransitionType::None);
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Idle);
    assert_eq!(state.confirmation_count, 0);
}

#[test]
fn tracker_confirming_true_evaluation_increases_counter() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(5));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(tracker.state(rule_id()).unwrap().confirmation_count, 2);
}

#[test]
fn tracker_confirming_reaches_threshold_opens_excursion() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(5));
    let t = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(10));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Active);
    // Confirmation count resets to 0 on open.
    assert_eq!(state.confirmation_count, 0);
}

#[test]
fn tracker_active_true_evaluation_continues_excursion() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionContinues);
    assert_eq!(t.excursion, Some(1));
}

#[test]
fn tracker_active_false_evaluation_starts_hysteresis() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 30), false, at(5));
    assert_eq!(t.kind, TransitionType::HysteresisStarted);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Hysteresis
    );
}

#[test]
fn tracker_hysteresis_true_evaluation_resumes_excursion() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 30), false, at(5));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(10));
    assert_eq!(t.kind, TransitionType::HysteresisResumed);
    assert_eq!(t.excursion, Some(1));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Active
    );
}

/// Opens at T0, enters hysteresis at T5, then feeds false every 5 minutes;
/// returns the minute of the hysteresis close.
fn hysteresis_close_minute(hysteresis_minutes: i32) -> i64 {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(1, hysteresis_minutes);
    let _ = tracker.process_evaluation(rule_id(), config, true, at(0));
    let _ = tracker.process_evaluation(rule_id(), config, false, at(5));
    for minute in (10..=120).step_by(5) {
        let t = tracker.process_evaluation(rule_id(), config, false, at(minute));
        if t.kind == TransitionType::ExcursionClosed {
            assert_eq!(t.close_reason, Some(CloseReason::Hysteresis));
            assert_eq!(t.excursion, Some(1));
            let state = tracker.state(rule_id()).unwrap();
            assert_eq!(state.state, TrackerStateKind::Idle);
            assert_eq!(state.active_excursion, None);
            assert_eq!(state.hysteresis_started_at, None);
            return minute;
        }
        assert_eq!(t.kind, TransitionType::None, "minute {minute}");
    }
    panic!("hysteresis of {hysteresis_minutes} minutes never closed");
}

#[test]
fn tracker_hysteresis_zero_closes_on_the_next_false_evaluation() {
    assert_eq!(hysteresis_close_minute(0), 10);
}

#[test]
fn tracker_hysteresis_negative_closes_like_zero() {
    assert_eq!(hysteresis_close_minute(-5), 10);
}

#[test]
fn tracker_hysteresis_window_measures_from_entry_not_the_last_evaluation() {
    assert_eq!(hysteresis_close_minute(5), 10);
    assert_eq!(hysteresis_close_minute(12), 20);
    assert_eq!(hysteresis_close_minute(30), 35);
}

#[test]
fn tracker_hysteresis_entry_records_its_start() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 30), false, at(5));
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 30), false, at(10));
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.hysteresis_started_at, Some(at(5)));
    assert_eq!(state.updated_at, at(10));
}

#[test]
fn tracker_hysteresis_reentry_resumes_and_restarts_the_window() {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(1, 30);
    let _ = tracker.process_evaluation(rule_id(), config, true, at(0));
    let _ = tracker.process_evaluation(rule_id(), config, false, at(5));
    let t = tracker.process_evaluation(rule_id(), config, true, at(20));
    assert_eq!(t.kind, TransitionType::HysteresisResumed);
    assert_eq!(t.excursion, Some(1));
    assert_eq!(
        tracker.state(rule_id()).unwrap().hysteresis_started_at,
        None
    );

    let t = tracker.process_evaluation(rule_id(), config, false, at(25));
    assert_eq!(t.kind, TransitionType::HysteresisStarted);
    // 30 minutes after the first entry, but only 10 after the second.
    let t = tracker.process_evaluation(rule_id(), config, false, at(35));
    assert_eq!(t.kind, TransitionType::None);
    let t = tracker.process_evaluation(rule_id(), config, false, at(55));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.excursion, Some(1));
}

#[test]
fn tracker_restore_without_hysteresis_start_adopts_updated_at() {
    let mut tracker = ExcursionTracker::new();
    tracker.restore_state(
        rule_id(),
        nocturne_alerts_core::excursion::TrackerState {
            state: TrackerStateKind::Hysteresis,
            confirmation_count: 0,
            active_excursion: Some(1),
            updated_at: at(5),
            hysteresis_started_at: None,
        },
    );
    assert_eq!(
        tracker.state(rule_id()).unwrap().hysteresis_started_at,
        Some(at(5))
    );
    let config = cfg(1, 30);
    let t = tracker.process_evaluation(rule_id(), config, false, at(30));
    assert_eq!(t.kind, TransitionType::None);
    let t = tracker.process_evaluation(rule_id(), config, false, at(35));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
}

#[test]
fn tracker_close_elapsed_hysteresis_closes_once_the_window_has_elapsed() {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(1, 30);
    let _ = tracker.process_evaluation(rule_id(), config, true, at(0));
    let _ = tracker.process_evaluation(rule_id(), config, false, at(5));

    let t = tracker.close_elapsed_hysteresis(rule_id(), config, at(34));
    assert_eq!(t.kind, TransitionType::None);
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Hysteresis);
    assert_eq!(
        state.updated_at,
        at(5),
        "a check that does not close writes nothing"
    );

    let t = tracker.close_elapsed_hysteresis(rule_id(), config, at(35));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::Hysteresis));
    assert_eq!(t.excursion, Some(1));
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Idle);
    assert_eq!(state.active_excursion, None);
    assert_eq!(state.hysteresis_started_at, None);
    assert_eq!(state.updated_at, at(35));
}

#[test]
fn tracker_close_elapsed_hysteresis_ignores_every_other_state() {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(2, 0);
    let t = tracker.close_elapsed_hysteresis(rule_id(), config, at(0));
    assert_eq!(t.kind, TransitionType::None);
    assert!(tracker.state(rule_id()).is_none());

    let _ = tracker.process_evaluation(rule_id(), config, true, at(0));
    let t = tracker.close_elapsed_hysteresis(rule_id(), config, at(60));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Confirming
    );

    let _ = tracker.process_evaluation(rule_id(), config, true, at(5));
    let t = tracker.close_elapsed_hysteresis(rule_id(), config, at(60));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Active
    );
}

#[test]
fn tracker_close_elapsed_hysteresis_matches_the_evaluation_expiry() {
    for minutes in [-5, 0, 5, 12, 30] {
        let expected = hysteresis_close_minute(minutes);
        let mut tracker = ExcursionTracker::new();
        let config = cfg(1, minutes);
        let _ = tracker.process_evaluation(rule_id(), config, true, at(0));
        let _ = tracker.process_evaluation(rule_id(), config, false, at(5));
        let closed_at = (10..=120)
            .step_by(5)
            .find(|&m| {
                tracker
                    .close_elapsed_hysteresis(rule_id(), config, at(m))
                    .kind
                    == TransitionType::ExcursionClosed
            })
            .unwrap();
        assert_eq!(closed_at, expected, "hysteresis_minutes {minutes}");
    }
}

#[test]
fn tracker_force_close_from_active_resets_to_idle() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::Manual, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::Manual));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn tracker_force_close_from_hysteresis_resets_to_idle() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 60), true, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 60), false, at(5));
    let t = tracker.force_close(rule_id(), CloseReason::AutoResolve, at(10));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::AutoResolve));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn tracker_force_close_from_idle_is_noop() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::Manual, at(5));
    assert_eq!(t.kind, TransitionType::None);
}

#[test]
fn tracker_force_close_from_confirming_is_noop() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::Manual, at(5));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Confirming
    );
}

#[test]
fn tracker_get_active_excursion_id_by_state() {
    let mut tracker = ExcursionTracker::new();
    // Idle: none.
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 60), false, at(0));
    assert_eq!(tracker.active_excursion_id(rule_id()), None);
    // Active: some.
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 60), true, at(5));
    assert_eq!(tracker.active_excursion_id(rule_id()), Some(1));
    // Hysteresis: some.
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 60), false, at(10));
    assert_eq!(tracker.active_excursion_id(rule_id()), Some(1));

    // Confirming: none.
    let rule2 = Uuid::from_u128(2);
    let mut tracker2 = ExcursionTracker::new();
    let _ = tracker2.process_evaluation(rule2, cfg(3, 0), true, at(0));
    assert_eq!(tracker2.active_excursion_id(rule2), None);
}

#[test]
fn tracker_reopen_after_close_creates_fresh_excursion_ordinal() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(5));
    let _ = tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(10));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(15));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(t.excursion, Some(2));
}

// ---------------------------------------------------------------------------
// tracker_age (domain tracker instances, distinct from the excursion tracker)
// ---------------------------------------------------------------------------

fn tracker_definition() -> Uuid {
    Uuid::from_u128(0xABCD)
}

fn tracker_ctx(reference_at: DateTime<Utc>) -> SensorContext {
    let mut ctx = SensorContext::default();
    ctx.active_trackers
        .insert(tracker_definition(), reference_at);
    ctx
}

fn tracker_age_payload(operator: &str, minutes: i32) -> Value {
    json!({
        "tracker_definition_id": tracker_definition().to_string(),
        "operator": operator,
        "minutes": minutes,
    })
}

#[test]
fn tracker_age_fires_at_and_after_threshold() {
    // Started 48h ago, threshold >= 48h.
    let ctx = tracker_ctx(base() - TimeDelta::hours(48));
    assert!(eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", 48 * 60),
        &ctx
    ));
}

#[test]
fn tracker_age_false_before_threshold() {
    let ctx = tracker_ctx(base() - TimeDelta::hours(47));
    assert!(!eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", 48 * 60),
        &ctx
    ));
}

#[test]
fn tracker_age_no_active_instance_is_false() {
    // Deliberately opposite to time_since_last_* cold-start infinity.
    let ctx = SensorContext::default();
    assert!(!eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", 0),
        &ctx
    ));
}

#[test]
fn tracker_age_other_definition_is_false() {
    let mut ctx = SensorContext::default();
    ctx.active_trackers
        .insert(Uuid::from_u128(0xFFFF), base() - TimeDelta::hours(48));
    assert!(!eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", 60),
        &ctx
    ));
}

#[test]
fn tracker_age_negative_minutes_pre_event_window() {
    // Event tracker scheduled 12h from now: elapsed is -720 minutes.
    // A "-24h before event" threshold (minutes = -1440) is already crossed;
    // a "-6h before event" threshold (minutes = -360) is not yet.
    let ctx = tracker_ctx(base() + TimeDelta::hours(12));
    assert!(eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", -1440),
        &ctx
    ));
    assert!(!eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", -360),
        &ctx
    ));
}

#[test]
fn tracker_age_unknown_operator_is_false() {
    let ctx = tracker_ctx(base() - TimeDelta::hours(48));
    assert!(!eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload("~", 60),
        &ctx
    ));
}

#[test]
fn tracker_age_missing_definition_id_is_nil_uuid_lookup() {
    // Absent tracker_definition_id parses as Uuid::nil (C# default); only a
    // context entry under the nil key would match.
    let ctx = tracker_ctx(base() - TimeDelta::hours(48));
    let payload = json!({ "operator": ">=", "minutes": 0 });
    assert!(!eval_payload(ConditionKind::TrackerAge, &payload, &ctx));
}

#[test]
fn tracker_age_wire_context_round_trips() {
    let wire = json!({
        "active_trackers": [
            {
                "tracker_definition_id": tracker_definition().to_string(),
                "reference_at": "2026-01-03T12:00:00Z",
            }
        ]
    });
    let ctx: SensorContext = serde_json::from_value(wire).expect("context parses");
    // base() is 2026-01-05T12:00:00Z — 48h after the reference.
    assert!(eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(">=", 48 * 60),
        &ctx
    ));
}

// ---------------------------------------------------------------------------
// Arithmetic edges: overflow degrades the one leaf, never panics
// ---------------------------------------------------------------------------

fn temp_basal_ctx(rate: Decimal, scheduled_rate: Decimal) -> SensorContext {
    SensorContext {
        active_temp_basal: Some(nocturne_alerts_core::context::TempBasalSnapshot {
            rate,
            scheduled_rate: Some(scheduled_rate),
            started_at: base(),
        }),
        ..Default::default()
    }
}

#[test]
fn temp_basal_percent_division_overflow_is_false() {
    let ctx = temp_basal_ctx(Decimal::MAX, d("0.5"));
    assert!(!eval_payload(
        ConditionKind::TempBasal,
        &json!({"metric": "percent_of_scheduled", "operator": ">=", "value": 0}),
        &ctx
    ));
}

#[test]
fn temp_basal_percent_multiplication_overflow_is_false() {
    let ctx = temp_basal_ctx(Decimal::MAX, d("2"));
    assert!(!eval_payload(
        ConditionKind::TempBasal,
        &json!({"metric": "percent_of_scheduled", "operator": ">=", "value": 0}),
        &ctx
    ));
}

#[test]
fn temp_basal_percent_in_range_still_compares() {
    let ctx = temp_basal_ctx(d("1.5"), d("1"));
    assert!(eval_payload(
        ConditionKind::TempBasal,
        &json!({"metric": "percent_of_scheduled", "operator": "==", "value": 150}),
        &ctx
    ));
}

#[test]
fn tracker_confirmation_count_saturates_on_restored_state() {
    let mut tracker = ExcursionTracker::new();
    tracker.restore_state(
        rule_id(),
        nocturne_alerts_core::excursion::TrackerState {
            state: TrackerStateKind::Confirming,
            confirmation_count: i32::MAX,
            active_excursion: None,
            updated_at: at(0),
            hysteresis_started_at: None,
        },
    );
    let t = tracker.process_evaluation(rule_id(), cfg(i32::MAX, 0), true, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
}

#[test]
fn tracker_excursion_ordinal_saturates() {
    let mut tracker = ExcursionTracker::new();
    tracker.set_next_excursion_ordinal(u32::MAX);
    let first = Uuid::from_u128(1);
    let second = Uuid::from_u128(2);
    let t = tracker.process_evaluation(first, cfg(1, 0), true, at(0));
    assert_eq!(t.excursion, Some(u32::MAX));
    let t = tracker.process_evaluation(second, cfg(1, 0), true, at(0));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(t.excursion, Some(u32::MAX));
}

// ---------------------------------------------------------------------------
// Timestamp domain: the .NET DateTime range 0001-01-01 ..= 9999-12-31
// ---------------------------------------------------------------------------

fn context_error(wire: Value) -> String {
    match serde_json::from_value::<SensorContext>(wire) {
        Ok(_) => panic!("context should be rejected"),
        Err(e) => e.to_string(),
    }
}

#[test]
fn context_rejects_a_timestamp_before_year_one_naming_only_the_field() {
    let err = context_error(json!({ "last_reading_at": "0000-12-31T23:59:59Z" }));
    assert!(err.contains("last_reading_at"), "{err}");
    assert!(!err.contains("0000-12-31"), "{err}");
}

#[test]
fn context_rejects_a_timestamp_after_year_9999_naming_only_the_field() {
    let err = context_error(json!({
        "active_temp_basal": { "rate": 1, "started_at": "+10000-01-01T00:00:00Z" }
    }));
    assert!(err.contains("started_at"), "{err}");
    assert!(!err.contains("10000"), "{err}");
}

#[test]
fn context_accepts_the_dotnet_datetime_bounds() {
    let wire = json!({
        "last_reading_at": "0001-01-01T00:00:00Z",
        "latest_timestamp": "9999-12-31T23:59:59.9999999Z",
    });
    serde_json::from_value::<SensorContext>(wire).expect("bounds are in range");
}

#[test]
fn elapsed_across_the_whole_domain_still_evaluates() {
    let ctx = SensorContext {
        latest_timestamp: Some(Utc.with_ymd_and_hms(1, 1, 1, 0, 0, 0).unwrap()),
        last_reading_at: Some(Utc.with_ymd_and_hms(1, 1, 1, 0, 0, 0).unwrap()),
        ..Default::default()
    };
    let now = Utc.with_ymd_and_hms(9999, 12, 31, 23, 59, 59).unwrap();
    assert!(eval_payload_at(
        ConditionKind::Staleness,
        &json!({"operator": ">", "value": 15}),
        &ctx,
        now
    ));
}

#[test]
fn hysteresis_expiry_past_the_calendar_does_not_panic() {
    let mut tracker = ExcursionTracker::new();
    tracker.restore_state(
        rule_id(),
        nocturne_alerts_core::excursion::TrackerState {
            state: TrackerStateKind::Hysteresis,
            confirmation_count: 0,
            active_excursion: Some(1),
            updated_at: DateTime::<Utc>::MAX_UTC,
            hysteresis_started_at: None,
        },
    );
    let t =
        tracker.process_evaluation(rule_id(), cfg(1, i32::MAX), false, DateTime::<Utc>::MAX_UTC);
    assert_eq!(t.kind, TransitionType::None);
}

// ---------------------------------------------------------------------------
// Unknown context enum values degrade only the fact that carries them
// ---------------------------------------------------------------------------

#[test]
fn unknown_context_enum_values_drop_only_their_facts() {
    let wire = json!({
        "latest_value": 55,
        "latest_timestamp": "2026-01-05T12:00:00Z",
        "last_reading_at": "2026-01-05T12:00:00Z",
        "trend_bucket": "sideways",
        "glucose_bucket": "off_the_chart",
        "active_pump_state": { "mode": "Turbo", "started_at": "2026-01-05T11:00:00Z" },
        "active_state_spans": [
            { "category": "NotACategory", "state": null, "started_at": "2026-01-05T11:00:00Z" },
            { "category": "Illness", "state": null, "started_at": "2026-01-05T11:00:00Z" },
        ],
    });
    let ctx: SensorContext = serde_json::from_value(wire).expect("context still parses");
    assert_eq!(ctx.trend_bucket, None);
    assert_eq!(ctx.glucose_bucket, None);
    assert!(ctx.active_pump_state.is_none());
    assert_eq!(ctx.active_state_spans.len(), 1);

    assert!(eval_payload(
        ConditionKind::Threshold,
        &json!({"direction": "below", "value": 70}),
        &ctx
    ));
    assert!(eval_payload(
        ConditionKind::StateSpanActive,
        &json!({"category": "Illness", "state": null, "is_active": true}),
        &ctx
    ));
    assert!(!eval_payload(
        ConditionKind::Trend,
        &json!({"bucket": "flat"}),
        &ctx
    ));
}

#[test]
fn context_decimal_errors_name_the_field_not_the_value() {
    let err = serde_json::from_str::<SensorContext>(r#"{ "iob_units": 123456e30 }"#)
        .expect_err("out-of-range decimal is rejected")
        .to_string();
    assert!(err.contains("iob_units"), "{err}");
    assert!(!err.contains("123456"), "{err}");
}

#[test]
fn context_type_errors_name_the_field_not_the_value() {
    let err = context_error(json!({ "latest_value": "55.5", "cob_grams": 12 }));
    assert!(err.contains("latest_value"), "{err}");
    assert!(!err.contains("55.5"), "{err}");

    let err = context_error(
        json!({ "active_temp_basal": { "rate": "0.85", "started_at": "2026-01-05T12:00:00Z" } }),
    );
    assert!(err.contains("active_temp_basal"), "{err}");
    assert!(!err.contains("0.85"), "{err}");
}

// ---------------------------------------------------------------------------
// Payload parsing strictness (System.Text.Json parity)
// ---------------------------------------------------------------------------

/// Parses raw JSON so number literals keep their exact spelling.
fn raw(json: &str) -> Value {
    serde_json::from_str(json).expect("valid JSON")
}

#[test]
fn uuid_payload_fields_accept_only_the_hyphenated_form() {
    let parses = |id: &str| {
        parse_payload(
            ConditionKind::AlertState,
            &json!({ "alert_id": id, "state": "firing" }),
        )
        .is_ok()
    };
    assert!(parses("00000000-0000-0000-0000-0000000000cc"));
    assert!(parses("00000000-0000-0000-0000-0000000000CC"));
    assert!(!parses("{00000000-0000-0000-0000-0000000000cc}"));
    assert!(!parses("000000000000000000000000000000cc"));
    assert!(!parses("urn:uuid:00000000-0000-0000-0000-0000000000cc"));
}

#[test]
fn enum_integers_must_fit_an_int() {
    let parses = |category: Value| {
        parse_payload(
            ConditionKind::StateSpanActive,
            &json!({ "category": category, "is_active": true }),
        )
        .is_ok()
    };
    assert!(parses(json!(2_147_483_647)));
    assert!(parses(json!(-1)));
    assert!(!parses(json!(2_147_483_648_i64)));
    assert!(!parses(json!(-2_147_483_649_i64)));
    assert!(!parses(json!("2147483648")));
    assert!(!parses(json!("4.0")));
    assert!(!parses(json!("0x4")));
}

#[test]
fn enum_integer_strings_allow_surrounding_whitespace_and_a_sign() {
    let ctx: SensorContext = serde_json::from_value(json!({
        "active_state_spans": [
            { "category": "Exercise", "state": null, "started_at": "2026-01-05T11:00:00Z" }
        ]
    }))
    .unwrap();
    for category in [" 4", "4 ", "+4"] {
        assert!(
            eval_payload(
                ConditionKind::StateSpanActive,
                &json!({ "category": category, "is_active": true }),
                &ctx
            ),
            "{category:?}"
        );
    }
}

fn threshold_value(literal: &str) -> Option<String> {
    let payload = raw(&format!(r#"{{"direction": "below", "value": {literal}}}"#));
    match parse_payload(ConditionKind::Threshold, &payload).ok()? {
        nocturne_alerts_core::model::Payload::Threshold(t) => Some(t.value.to_string()),
        _ => None,
    }
}

#[test]
fn decimal_literals_round_like_system_text_json() {
    // Expected strings are .NET `decimal.ToString()` of the STJ-parsed value.
    let cases = [
        ("1e-30", "0.0000000000000000000000000000"),
        ("-1e-300", "0.0000000000000000000000000000"),
        ("5e-29", "0.0000000000000000000000000000"),
        ("1.5e-28", "0.0000000000000000000000000002"),
        ("2.5e-28", "0.0000000000000000000000000002"),
        (
            "0.00000000000000000000000000025",
            "0.0000000000000000000000000002",
        ),
        ("1.23e-27", "0.0000000000000000000000000012"),
        ("0.5e1", "5"),
        ("1E+28", "10000000000000000000000000000"),
        ("1.50", "1.50"),
        (
            "12345678901234567890123456789.5",
            "12345678901234567890123456790",
        ),
        (
            "52.9920801916023291802352150425",
            "52.992080191602329180235215043",
        ),
        (
            "4.76862120714677194819717187865",
            "4.7686212071467719481971718787",
        ),
        (
            "922.94429613655535840313700185E+16",
            "9229442961365553584.031370018",
        ),
    ];
    for (literal, expected) in cases {
        assert_eq!(
            threshold_value(literal).as_deref(),
            Some(expected),
            "{literal}"
        );
    }
    assert_eq!(threshold_value("1e29"), None);
    assert_eq!(threshold_value("7.9228162514264337593543950336e28"), None);
}

fn not_chain(levels: usize, innermost: Value) -> Value {
    (0..levels).fold(
        innermost,
        |child, _| json!({ "type": "not", "not": { "child": child } }),
    )
}

fn threshold_node() -> Value {
    json!({ "type": "threshold", "threshold": { "direction": "below", "value": 70 } })
}

#[test]
fn node_depth_limit_counts_typed_values_like_system_text_json() {
    // 30 not levels (60 objects) + threshold node and payload = 62.
    assert!(Node::parse_structure(&not_chain(30, threshold_node())).is_ok());
    // 63 typed objects is the most STJ builds.
    assert!(Node::parse_structure(&not_chain(31, json!({ "type": "threshold" }))).is_ok());
    assert!(Node::parse_structure(&not_chain(31, threshold_node())).is_err());
    // A condition list is a typed frame too: 60 + node + payload + list + node = 64.
    let composite = json!({
        "type": "composite",
        "composite": { "operator": "and", "conditions": [{ "type": "threshold" }] }
    });
    assert!(Node::parse_structure(&not_chain(30, composite)).is_err());
}

#[test]
fn node_depth_limit_counts_ignored_properties_as_plain_json() {
    let with_arrays = |arrays: usize| {
        let mut x = json!(1);
        for _ in 0..arrays {
            x = json!([x]);
        }
        json!({ "type": "threshold", "threshold": { "direction": "below", "value": 1, "x": x } })
    };
    // 62 typed objects + 2 untyped arrays = 64 nested containers.
    assert!(Node::parse_structure(&not_chain(30, with_arrays(2))).is_ok());
    assert!(Node::parse_structure(&not_chain(30, with_arrays(3))).is_err());
}

#[test]
fn payload_depth_counts_from_the_payload_document() {
    let payload = |levels| json!({ "child": not_chain(levels, json!({ "type": "threshold", "threshold": {} })) });
    // payload + 60 + node + payload = 63.
    assert!(parse_payload_structure(ConditionKind::Not, &payload(30)).is_ok());
    assert!(parse_payload_structure(ConditionKind::Not, &payload(31)).is_err());
}

#[test]
fn very_deep_nodes_are_a_parse_error_not_a_stack_overflow() {
    assert!(Node::parse(&not_chain(500, threshold_node())).is_err());
}
