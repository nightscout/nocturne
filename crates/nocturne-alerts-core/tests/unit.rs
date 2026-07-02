//! Representative port of the C# evaluator/tracker edge tests
//! (`ConditionEvaluatorTests.cs`, `ExcursionTrackerTests.cs`), covering null
//! permutations and state-machine edges the corpus may not pin individually.

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::eval::{Env, eval_kind, eval_node};
use nocturne_alerts_core::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerStateKind, TransitionType,
};
use nocturne_alerts_core::model::{ConditionKind, Node, parse_payload};
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
    let mut env = Env {
        now,
        rule_id: Uuid::nil(),
        ctx,
        timers: &mut timers,
    };
    eval_kind(kind, Some(&parsed), kind.wire(), &mut env)
}

fn eval_tree(node_json: &Value, ctx: &SensorContext) -> bool {
    let node = Node::parse(node_json).expect("node parses");
    let mut timers = TimerStore::new();
    let mut env = Env {
        now: base(),
        rule_id: Uuid::nil(),
        ctx,
        timers: &mut timers,
    };
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
    tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(3, 0), false, at(5));
    assert_eq!(t.kind, TransitionType::None);
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Idle);
    assert_eq!(state.confirmation_count, 0);
}

#[test]
fn tracker_confirming_true_evaluation_increases_counter() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(5));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(tracker.state(rule_id()).unwrap().confirmation_count, 2);
}

#[test]
fn tracker_confirming_reaches_threshold_opens_excursion() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
    tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(5));
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
    tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionContinues);
    assert_eq!(t.excursion, Some(1));
}

#[test]
fn tracker_active_false_evaluation_starts_hysteresis() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(0));
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
    tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(0));
    tracker.process_evaluation(rule_id(), cfg(1, 30), false, at(5));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 30), true, at(10));
    assert_eq!(t.kind, TransitionType::HysteresisResumed);
    assert_eq!(t.excursion, Some(1));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Active
    );
}

#[test]
fn tracker_hysteresis_false_before_expiry_no_transition() {
    // The expiry proxy is the previous evaluation's updated_at: with a
    // 10-minute window and 5-minute gaps the window keeps sliding.
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(1, 10), true, at(0));
    tracker.process_evaluation(rule_id(), cfg(1, 10), false, at(5));
    let t = tracker.process_evaluation(rule_id(), cfg(1, 10), false, at(10));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Hysteresis
    );
}

#[test]
fn tracker_hysteresis_false_after_expiry_closes_with_hysteresis_reason() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(1, 10), true, at(0));
    tracker.process_evaluation(rule_id(), cfg(1, 10), false, at(5));
    // Gap >= hysteresis window: now >= prev updated_at + 10 min.
    let t = tracker.process_evaluation(rule_id(), cfg(1, 10), false, at(15));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::Hysteresis));
    assert_eq!(t.excursion, Some(1));
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Idle);
    assert_eq!(state.active_excursion, None);
}

#[test]
fn tracker_force_close_from_active_resets_to_idle() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
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
    tracker.process_evaluation(rule_id(), cfg(1, 60), true, at(0));
    tracker.process_evaluation(rule_id(), cfg(1, 60), false, at(5));
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
    tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::Manual, at(5));
    assert_eq!(t.kind, TransitionType::None);
}

#[test]
fn tracker_force_close_from_confirming_is_noop() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(3, 0), true, at(0));
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
    tracker.process_evaluation(rule_id(), cfg(1, 60), false, at(0));
    assert_eq!(tracker.active_excursion_id(rule_id()), None);
    // Active: some.
    tracker.process_evaluation(rule_id(), cfg(1, 60), true, at(5));
    assert_eq!(tracker.active_excursion_id(rule_id()), Some(1));
    // Hysteresis: some.
    tracker.process_evaluation(rule_id(), cfg(1, 60), false, at(10));
    assert_eq!(tracker.active_excursion_id(rule_id()), Some(1));

    // Confirming: none.
    let rule2 = Uuid::from_u128(2);
    let mut tracker2 = ExcursionTracker::new();
    tracker2.process_evaluation(rule2, cfg(3, 0), true, at(0));
    assert_eq!(tracker2.active_excursion_id(rule2), None);
}

#[test]
fn tracker_reopen_after_close_creates_fresh_excursion_ordinal() {
    let mut tracker = ExcursionTracker::new();
    tracker.process_evaluation(rule_id(), cfg(1, 0), true, at(0));
    tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(5));
    tracker.process_evaluation(rule_id(), cfg(1, 0), false, at(10));
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
