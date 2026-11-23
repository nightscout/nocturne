//! Evaluator and tracker edge cases, covering null permutations and
//! state-machine edges the corpus may not pin individually.

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

use nocturne_alerts_core::context::{SensorContext, TrackerReference};
use nocturne_alerts_core::engine::{
    EngineState, Evaluation, OnUnknown, Rule, WireRule, evaluate_rule,
};
use nocturne_alerts_core::enums::{GlucoseBucket, StateSpanCategory, TrendBucket, WireEnum};
use nocturne_alerts_core::eval::{
    DEFAULT_GLUCOSE_FRESHNESS, Env, eval_node, eval_payload as eval_parsed,
};
use nocturne_alerts_core::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerState, TrackerStateKind,
    TransitionType,
};
use nocturne_alerts_core::model::{ConditionKind, Node, ParseResult, Reason, parse_payload};
use nocturne_alerts_core::sustained::{NOT_TRUE_LEASE, TimerState, TimerStore};
use nocturne_alerts_core::truth::Truth::{self, False, True, Unknown};

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

fn eval_payload(kind: ConditionKind, payload: &Value, ctx: &SensorContext) -> Truth {
    eval_payload_at(kind, payload, ctx, base())
}

fn eval_payload_at(
    kind: ConditionKind,
    payload: &Value,
    ctx: &SensorContext,
    now: DateTime<Utc>,
) -> Truth {
    let parsed = parse_payload(kind, payload).expect("payload parses");
    let mut timers = TimerStore::new();
    let mut env = Env::new(now, Uuid::nil(), ctx, &mut timers);
    eval_parsed(&parsed, kind.name(), &mut env)
}

fn eval_tree(node_json: &Value, ctx: &SensorContext) -> Truth {
    let node = Node::parse(node_json).expect("node parses");
    let mut timers = TimerStore::new();
    let mut env = Env::new(base(), Uuid::nil(), ctx, &mut timers);
    let root = node.kind().name();
    eval_node(&node, root, &mut env)
}

// ---------------------------------------------------------------------------
// ThresholdEvaluator
// ---------------------------------------------------------------------------

#[test]
fn threshold_below_triggers_when_value_below_threshold() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(Some("65"), Some("0"));
    assert_eq!(True, eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_below_does_not_trigger_when_value_above_threshold() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(Some("85"), Some("0"));
    assert_eq!(
        False,
        eval_payload(ConditionKind::Threshold, &payload, &ctx)
    );
}

#[test]
fn threshold_below_exact_boundary_returns_false() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(Some("70"), Some("0"));
    assert_eq!(
        False,
        eval_payload(ConditionKind::Threshold, &payload, &ctx)
    );
}

#[test]
fn threshold_above_triggers_when_value_above_threshold() {
    let payload = json!({"direction": "above", "value": 250});
    let ctx = glucose_ctx(Some("260"), Some("0"));
    assert_eq!(True, eval_payload(ConditionKind::Threshold, &payload, &ctx));
}

#[test]
fn threshold_above_exact_boundary_returns_false() {
    let payload = json!({"direction": "above", "value": 250});
    let ctx = glucose_ctx(Some("250"), Some("0"));
    assert_eq!(
        False,
        eval_payload(ConditionKind::Threshold, &payload, &ctx)
    );
}

#[test]
fn threshold_null_latest_value_is_unknown() {
    let payload = json!({"direction": "below", "value": 70});
    let ctx = glucose_ctx(None, Some("0"));
    assert_eq!(
        Unknown,
        eval_payload(ConditionKind::Threshold, &payload, &ctx)
    );
}

// ---------------------------------------------------------------------------
// RateOfChangeEvaluator
// ---------------------------------------------------------------------------

#[test]
fn rate_of_change_falling_triggers_at_negative_threshold() {
    // rate = -3.0, threshold = 3.0 => -3.0 <= -3.0 => true
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("-3.0"));
    assert_eq!(
        True,
        eval_payload(ConditionKind::RateOfChange, &payload, &ctx)
    );
}

#[test]
fn rate_of_change_falling_triggers_below_negative_threshold() {
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("-4.0"));
    assert_eq!(
        True,
        eval_payload(ConditionKind::RateOfChange, &payload, &ctx)
    );
}

#[test]
fn rate_of_change_falling_does_not_trigger_above_negative_threshold() {
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("-2.0"));
    assert_eq!(
        False,
        eval_payload(ConditionKind::RateOfChange, &payload, &ctx)
    );
}

#[test]
fn rate_of_change_rising_triggers_at_threshold() {
    let payload = json!({"direction": "rising", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("3.0"));
    assert_eq!(
        True,
        eval_payload(ConditionKind::RateOfChange, &payload, &ctx)
    );
}

#[test]
fn rate_of_change_rising_does_not_trigger_below_threshold() {
    let payload = json!({"direction": "rising", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), Some("2.0"));
    assert_eq!(
        False,
        eval_payload(ConditionKind::RateOfChange, &payload, &ctx)
    );
}

#[test]
fn rate_of_change_null_trend_rate_is_unknown() {
    let payload = json!({"direction": "falling", "rate": 3.0});
    let ctx = glucose_ctx(Some("100"), None);
    assert_eq!(
        Unknown,
        eval_payload(ConditionKind::RateOfChange, &payload, &ctx)
    );
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
    assert_eq!(True, eval_tree(&composite_tree("and"), &ctx));
}

#[test]
fn composite_and_one_false_returns_false() {
    let ctx = glucose_ctx(Some("60"), Some("-1.0"));
    assert_eq!(False, eval_tree(&composite_tree("and"), &ctx));
}

#[test]
fn composite_or_any_true_returns_true() {
    let ctx = glucose_ctx(Some("100"), Some("-4.0"));
    assert_eq!(True, eval_tree(&composite_tree("or"), &ctx));
}

#[test]
fn composite_or_all_false_returns_false() {
    let ctx = glucose_ctx(Some("100"), Some("-1.0"));
    assert_eq!(False, eval_tree(&composite_tree("or"), &ctx));
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
    assert_eq!(True, eval_tree(&tree, &ctx));
}

// ---------------------------------------------------------------------------
// not / sustained edge cases
// ---------------------------------------------------------------------------

#[test]
fn sustained_timer_runs_behind_a_false_and_sibling() {
    let tree = json!({
        "type": "composite",
        "composite": {"operator": "and", "conditions": [
            {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
            {"type": "sustained", "sustained": {"minutes": 10, "child":
                {"type": "threshold", "threshold": {"direction": "below", "value": 120}}}}
        ]}
    });
    let node = Node::parse(&tree).expect("node parses");
    let mut timers = TimerStore::new();
    let at = |now: DateTime<Utc>, glucose: &str, timers: &mut TimerStore| {
        let ctx = SensorContext {
            latest_timestamp: Some(now),
            last_reading_at: Some(now),
            ..glucose_ctx(Some(glucose), Some("0"))
        };
        let mut env = Env::new(now, Uuid::nil(), &ctx, timers);
        eval_node(&node, "composite", &mut env)
    };

    // The first row is false, yet the sustained row starts its clock.
    assert_eq!(False, at(base(), "100", &mut timers));
    let path = "composite[1].sustained";
    assert_eq!(
        timers.snapshot_for_rule(Uuid::nil()).collect::<Vec<_>>(),
        vec![(path, TimerState::Running(base()))]
    );
    // Ten minutes on, with the first row now true, the rule holds at once.
    assert_eq!(True, at(base() + TimeDelta::minutes(10), "65", &mut timers));
    // Both rows false: the run ends although the first row is false.
    assert_eq!(
        False,
        at(base() + TimeDelta::minutes(15), "150", &mut timers)
    );
    assert_eq!(
        timers.snapshot_for_rule(Uuid::nil()).collect::<Vec<_>>(),
        vec![(path, TimerState::NotTrue(base() + TimeDelta::minutes(15)))]
    );
}

// ---------------------------------------------------------------------------
// Glucose freshness (§3)
// ---------------------------------------------------------------------------

/// Each leaf that judges freshness, true on a fresh `glucose_ctx` reading.
fn freshness_leaves() -> Vec<(ConditionKind, Value, SensorContext)> {
    let glucose = |trend_bucket, glucose_bucket| SensorContext {
        trend_bucket,
        glucose_bucket,
        ..glucose_ctx(Some("60"), Some("-3"))
    };
    vec![
        (
            ConditionKind::Threshold,
            json!({"direction": "below", "value": 70}),
            glucose(None, None),
        ),
        (
            ConditionKind::RateOfChange,
            json!({"direction": "falling", "rate": 2}),
            glucose(None, None),
        ),
        (
            ConditionKind::Trend,
            json!({"bucket": "falling_fast"}),
            glucose(Some(TrendBucket::FallingFast), None),
        ),
        (
            ConditionKind::GlucoseBucket,
            json!({"buckets": ["low"]}),
            glucose(None, Some(GlucoseBucket::Low)),
        ),
    ]
}

#[test]
fn a_glucose_fact_is_fresh_up_to_exactly_eleven_minutes_and_unknown_after() {
    for (kind, payload, ctx) in freshness_leaves() {
        let at = |age: TimeDelta| eval_payload_at(kind, &payload, &ctx, base() + age);
        assert_eq!(True, at(TimeDelta::zero()), "{kind:?} new");
        assert_eq!(True, at(TimeDelta::minutes(11)), "{kind:?} at the bound");
        assert_eq!(
            Unknown,
            at(TimeDelta::minutes(11) + TimeDelta::nanoseconds(1)),
            "{kind:?} past the bound"
        );
    }
}

#[test]
fn a_rules_freshness_bound_replaces_the_default() {
    for (kind, payload, ctx) in freshness_leaves() {
        let parsed = parse_payload(kind, &payload).expect("payload parses");
        let at = |age: TimeDelta, bound: TimeDelta| {
            let mut timers = TimerStore::new();
            let mut env = Env::new(base() + age, Uuid::nil(), &ctx, &mut timers)
                .with_glucose_freshness(bound);
            eval_parsed(&parsed, kind.name(), &mut env)
        };
        let thirty = TimeDelta::minutes(30);
        assert_eq!(True, at(TimeDelta::minutes(20), thirty), "{kind:?}");
        assert_eq!(True, at(thirty, thirty), "{kind:?}");
        assert_eq!(
            Unknown,
            at(thirty + TimeDelta::nanoseconds(1), thirty),
            "{kind:?}"
        );
        assert_eq!(
            Unknown,
            at(TimeDelta::minutes(6), TimeDelta::minutes(5)),
            "{kind:?}"
        );
    }
}

/// A reading that arrives late is as fresh as its arrival: its age runs from
/// when the host received it, not from its own timestamp.
#[test]
fn a_late_reading_is_fresh_from_its_arrival() {
    for (kind, payload, ctx) in freshness_leaves() {
        let arrived = base() + TimeDelta::minutes(20);
        let late = SensorContext {
            latest_received_at: Some(arrived),
            ..ctx
        };
        let at = |age: TimeDelta| eval_payload_at(kind, &payload, &late, arrived + age);
        assert_eq!(True, at(TimeDelta::zero()), "{kind:?} on arrival");
        assert_eq!(True, at(TimeDelta::minutes(11)), "{kind:?} at the bound");
        assert_eq!(
            Unknown,
            at(TimeDelta::minutes(11) + TimeDelta::nanoseconds(1)),
            "{kind:?} past the bound"
        );
    }
}

#[test]
fn a_glucose_fact_without_a_reading_time_is_not_judged_stale() {
    let ctx = SensorContext {
        latest_timestamp: None,
        ..glucose_ctx(Some("60"), None)
    };
    let payload = json!({"direction": "below", "value": 70});
    let a_day_on = base() + TimeDelta::days(1);
    assert_eq!(
        True,
        eval_payload_at(ConditionKind::Threshold, &payload, &ctx, a_day_on)
    );
}

#[test]
fn signal_loss_and_staleness_read_an_old_reading_as_ever() {
    let ctx = glucose_ctx(Some("100"), None);
    let an_hour_on = base() + TimeDelta::hours(1);
    assert_eq!(
        True,
        eval_payload_at(
            ConditionKind::SignalLoss,
            &json!({"timeout_minutes": 20}),
            &ctx,
            an_hour_on
        )
    );
    assert_eq!(
        True,
        eval_payload_at(
            ConditionKind::Staleness,
            &json!({"operator": ">", "value": 30}),
            &ctx,
            an_hour_on
        )
    );
}

fn held_rule(condition_type: ConditionKind, condition_params: Value) -> Rule {
    Rule {
        id: rule_id(),
        condition_type,
        condition_params,
        hysteresis_minutes: 0,
        auto_resolve_enabled: false,
        auto_resolve_params: None,
        on_unknown: OnUnknown::Hold,
        glucose_freshness: DEFAULT_GLUCOSE_FRESHNESS,
        enabled_at: None,
    }
}

#[test]
fn signal_loss_or_a_stale_threshold_opens_once_the_signal_is_lost() {
    let rule = held_rule(
        ConditionKind::Composite,
        json!({"operator": "or", "conditions": [
            {"type": "signal_loss", "signal_loss": {"timeout_minutes": 20}},
            {"type": "threshold", "threshold": {"direction": "below", "value": 70}}
        ]}),
    );
    let ctx = glucose_ctx(Some("100"), None);
    let mut state = EngineState::new();
    let mut at = |minutes| {
        evaluate_rule(
            &rule,
            &ctx,
            base() + TimeDelta::minutes(minutes),
            &mut state,
            false,
        )
        .evaluation
        .expect("evaluable")
    };
    assert_eq!(False, at(0).root);
    let outage = at(15);
    assert_eq!(Unknown, outage.root);
    assert_eq!(TransitionType::None, outage.transition.kind);
    let lost = at(20);
    assert_eq!(True, lost.root);
    assert_eq!(TransitionType::ExcursionOpened, lost.transition.kind);
}

#[test]
fn a_sustained_run_completes_only_on_a_fresh_reading() {
    let rule = held_rule(
        ConditionKind::Sustained,
        json!({"minutes": 60, "child":
            {"type": "threshold", "threshold": {"direction": "above", "value": 250}}}),
    );
    let old = glucose_ctx(Some("300"), None);
    let mut state = EngineState::new();
    evaluate_rule(&rule, &old, base(), &mut state, false);
    let outage = evaluate_rule(
        &rule,
        &old,
        base() + TimeDelta::minutes(61),
        &mut state,
        false,
    )
    .evaluation
    .expect("evaluable");
    assert_eq!(Unknown, outage.root);
    assert_eq!(TransitionType::None, outage.transition.kind);
    assert!(outage.timer_ops.is_empty());
    let now = base() + TimeDelta::minutes(62);
    let fresh = SensorContext {
        latest_timestamp: Some(now),
        last_reading_at: Some(now),
        ..old
    };
    let resumed = evaluate_rule(&rule, &fresh, now, &mut state, false)
        .evaluation
        .expect("evaluable");
    assert_eq!(TransitionType::ExcursionOpened, resumed.transition.kind);
}

#[test]
fn glucose_freshness_minutes_is_a_whole_number_of_minutes_up_to_a_day() {
    let wire = |minutes: Value| {
        let wire: WireRule = serde_json::from_value(json!({
            "id": rule_id(),
            "condition_type": "threshold",
            "condition_params": {"direction": "below", "value": 70},
            "glucose_freshness_minutes": minutes,
        }))
        .map_err(|e| e.to_string())?;
        Rule::try_from(wire)
    };
    assert_eq!(
        wire(Value::Null).unwrap().glucose_freshness,
        DEFAULT_GLUCOSE_FRESHNESS
    );
    for (minutes, bound) in [(1, 1), (30, 30), (1440, 1440)] {
        assert_eq!(
            wire(json!(minutes)).unwrap().glucose_freshness,
            TimeDelta::minutes(bound)
        );
    }
    for bad in [
        json!(0),
        json!(-5),
        json!(1441),
        json!(i64::from(i32::MAX) + 1),
    ] {
        assert_eq!(
            wire(bad.clone()).unwrap_err(),
            "glucose_freshness_minutes must be a whole number of minutes from 1 to 1440",
            "{bad}"
        );
    }
    assert!(wire(json!(1.5)).is_err());
    assert!(wire(json!("11")).is_err());
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
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::Staleness,
            &json!({"operator": ">", "value": 15}),
            &ctx
        )
    );
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::Staleness,
            &json!({"operator": ">=", "value": 15}),
            &ctx
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::Staleness,
            &json!({"operator": "<", "value": 15}),
            &ctx
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::Staleness,
            &json!({"operator": "==", "value": 15}),
            &ctx
        )
    );
}

#[test]
fn staleness_cold_start_is_unknown_before_infinity() {
    let ctx = SensorContext::default();
    assert_eq!(
        Unknown,
        eval_payload(
            ConditionKind::Staleness,
            &json!({"operator": ">", "value": 15}),
            &ctx
        )
    );
}

/// §5.4: an `enabled_at` after `now` is an anchor ahead of the clock, as a
/// reading from the future is: up to 5 minutes ahead, exactly 5 included, it
/// has passed no time, and further ahead the leaf is unknown.
#[test]
fn an_enablement_after_now_follows_the_clock_tolerance() {
    let ctx = SensorContext::default();
    let leaf = |ahead: TimeDelta, kind: ConditionKind, payload: Value| {
        let parsed = parse_payload(kind, &payload).expect("payload parses");
        let mut timers = TimerStore::new();
        let mut env =
            Env::new(base(), Uuid::nil(), &ctx, &mut timers).with_enabled_at(Some(base() + ahead));
        eval_parsed(&parsed, kind.name(), &mut env)
    };
    let staleness = |ahead: TimeDelta| {
        move |op: &str| {
            leaf(
                ahead,
                ConditionKind::Staleness,
                json!({"operator": op, "value": 0}),
            )
        }
    };
    let signal_loss = |ahead| {
        leaf(
            ahead,
            ConditionKind::SignalLoss,
            json!({"timeout_minutes": 1}),
        )
    };

    for within in [TimeDelta::minutes(3), TimeDelta::minutes(5)] {
        assert_eq!(False, signal_loss(within));
        assert_eq!(
            [False, True, True, False, True],
            [">", ">=", "==", "<", "<="].map(staleness(within))
        );
    }
    let beyond = TimeDelta::minutes(5) + TimeDelta::seconds(1);
    assert_eq!(Unknown, signal_loss(beyond));
    assert_eq!(
        [Unknown; 5],
        [">", ">=", "==", "<", "<="].map(staleness(beyond))
    );
}

#[test]
fn time_since_last_carb_missing_anchor_is_positive_infinity() {
    let ctx = SensorContext::default();
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::TimeSinceLastCarb,
            &json!({"operator": ">", "minutes": 30}),
            &ctx
        )
    );
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::TimeSinceLastCarb,
            &json!({"operator": ">=", "minutes": 30}),
            &ctx
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::TimeSinceLastCarb,
            &json!({"operator": "<", "minutes": 30}),
            &ctx
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::TimeSinceLastCarb,
            &json!({"operator": "==", "minutes": 30}),
            &ctx
        )
    );
}

#[test]
fn temp_basal_percent_with_zero_scheduled_rate_is_unknown() {
    let ctx = SensorContext {
        active_temp_basal: Some(nocturne_alerts_core::context::TempBasalSnapshot {
            rate: d("1.5"),
            scheduled_rate: Some(Decimal::ZERO),
            started_at: base(),
        }),
        ..Default::default()
    };
    assert_eq!(
        Unknown,
        eval_payload(
            ConditionKind::TempBasal,
            &json!({"metric": "percent_of_scheduled", "operator": ">=", "value": 50}),
            &ctx
        )
    );
}

#[test]
fn sleep_session_active_matches_the_asserted_side_of_the_signal() {
    let active = SensorContext {
        sleep_session_active: true,
        ..Default::default()
    };
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::SleepSessionActive,
            &json!({"is_active": true}),
            &active
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::SleepSessionActive,
            &json!({"is_active": false}),
            &active
        )
    );

    let idle = SensorContext::default();
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::SleepSessionActive,
            &json!({"is_active": false}),
            &idle
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::SleepSessionActive,
            &json!({"is_active": true}),
            &idle
        )
    );
}

#[test]
fn loop_stale_null_timestamp_with_guard_set_is_unknown() {
    // Guard passes but the cycle timestamp is null: no infinity convention.
    let ctx = SensorContext {
        has_ever_aps_cycled: true,
        last_aps_cycle_at: None,
        ..Default::default()
    };
    assert_eq!(
        Unknown,
        eval_payload(
            ConditionKind::LoopStale,
            &json!({"operator": ">", "minutes": 15}),
            &ctx
        )
    );
}

// ---------------------------------------------------------------------------
// ExcursionTracker
// ---------------------------------------------------------------------------

fn rule_id() -> Uuid {
    Uuid::from_u128(1)
}

fn cfg(hysteresis_minutes: i32) -> TrackerRuleConfig {
    TrackerRuleConfig { hysteresis_minutes }
}

fn at(minutes: i64) -> DateTime<Utc> {
    base() + TimeDelta::minutes(minutes)
}

#[test]
fn tracker_idle_false_evaluation_stays_idle() {
    let mut tracker = ExcursionTracker::new();
    let t = tracker.process_evaluation(rule_id(), cfg(0), false, false, at(0));
    assert_eq!(t.kind, TransitionType::None);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn tracker_idle_true_opens_an_excursion() {
    let mut tracker = ExcursionTracker::new();
    let t = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(0));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(t.excursion, Some(1));
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.state, TrackerStateKind::Active);
}

#[test]
fn tracker_active_true_evaluation_continues_excursion() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionContinues);
    assert_eq!(t.excursion, Some(1));
}

#[test]
fn tracker_active_false_evaluation_starts_hysteresis() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(30), true, false, at(0));
    let t = tracker.process_evaluation(rule_id(), cfg(30), false, false, at(5));
    assert_eq!(t.kind, TransitionType::HysteresisStarted);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Hysteresis
    );
}

#[test]
fn tracker_hysteresis_true_evaluation_resumes_excursion() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(30), true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(30), false, false, at(5));
    let t = tracker.process_evaluation(rule_id(), cfg(30), true, false, at(10));
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
    let config = cfg(hysteresis_minutes);
    let _ = tracker.process_evaluation(rule_id(), config, true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), config, false, false, at(5));
    for minute in (10..=120).step_by(5) {
        let t = tracker.process_evaluation(rule_id(), config, false, false, at(minute));
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
    let _ = tracker.process_evaluation(rule_id(), cfg(30), true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(30), false, false, at(5));
    let _ = tracker.process_evaluation(rule_id(), cfg(30), false, false, at(10));
    let state = tracker.state(rule_id()).unwrap();
    assert_eq!(state.hysteresis_started_at, Some(at(5)));
    assert_eq!(state.updated_at, at(10));
}

#[test]
fn tracker_hysteresis_reentry_resumes_and_restarts_the_window() {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(30);
    let _ = tracker.process_evaluation(rule_id(), config, true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), config, false, false, at(5));
    let t = tracker.process_evaluation(rule_id(), config, true, false, at(20));
    assert_eq!(t.kind, TransitionType::HysteresisResumed);
    assert_eq!(t.excursion, Some(1));
    assert_eq!(
        tracker.state(rule_id()).unwrap().hysteresis_started_at,
        None
    );

    let t = tracker.process_evaluation(rule_id(), config, false, false, at(25));
    assert_eq!(t.kind, TransitionType::HysteresisStarted);
    // 30 minutes after the first entry, but only 10 after the second.
    let t = tracker.process_evaluation(rule_id(), config, false, false, at(35));
    assert_eq!(t.kind, TransitionType::None);
    let t = tracker.process_evaluation(rule_id(), config, false, false, at(55));
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
            active_excursion: Some(1),
            last_excursion: 1,
            updated_at: at(5),
            hysteresis_started_at: None,
            awaiting_rearm: false,
            held_since: None,
        },
    );
    assert_eq!(
        tracker.state(rule_id()).unwrap().hysteresis_started_at,
        Some(at(5))
    );
    let config = cfg(30);
    let t = tracker.process_evaluation(rule_id(), config, false, false, at(30));
    assert_eq!(t.kind, TransitionType::None);
    let t = tracker.process_evaluation(rule_id(), config, false, false, at(35));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
}

#[test]
fn tracker_close_elapsed_hysteresis_closes_once_the_window_has_elapsed() {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(30);
    let _ = tracker.process_evaluation(rule_id(), config, true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), config, false, false, at(5));

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
    let config = cfg(0);
    let t = tracker.close_elapsed_hysteresis(rule_id(), config, at(0));
    assert_eq!(t.kind, TransitionType::None);
    assert!(tracker.state(rule_id()).is_none());

    let _ = tracker.process_evaluation(rule_id(), config, true, false, at(0));
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
        let config = cfg(minutes);
        let _ = tracker.process_evaluation(rule_id(), config, true, false, at(0));
        let _ = tracker.process_evaluation(rule_id(), config, false, false, at(5));
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
    let _ = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::Manual, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::Manual));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn close_reason_rule_disabled_round_trips_through_wire() {
    assert_eq!(
        CloseReason::from_wire("rule-disabled"),
        Some(CloseReason::RuleDisabled)
    );
    assert_eq!(CloseReason::RuleDisabled.wire(), "rule-disabled");
}

#[test]
fn tracker_force_close_with_rule_disabled_resets_to_idle() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::RuleDisabled, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::RuleDisabled));
    assert!(!tracker.awaiting_rearm(rule_id()));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn tracker_force_close_from_hysteresis_resets_to_idle() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(60), true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(60), false, false, at(5));
    let t = tracker.force_close(rule_id(), CloseReason::AutoResolve, at(10));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert_eq!(t.close_reason, Some(CloseReason::AutoResolve));
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Idle
    );
}

#[test]
fn tracker_auto_resolve_of_an_active_excursion_waits_for_a_false_evaluation() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(0));
    let _ = tracker.force_close(rule_id(), CloseReason::AutoResolve, at(0));
    assert!(tracker.awaiting_rearm(rule_id()));

    for minute in [1, 2, 3] {
        let t = tracker.process_evaluation(rule_id(), cfg(0), true, true, at(minute));
        assert_eq!(t.kind, TransitionType::None, "both still met at {minute}");
    }
    let t = tracker.process_evaluation(rule_id(), cfg(0), false, true, at(4));
    assert_eq!(t.kind, TransitionType::None);
    assert!(!tracker.awaiting_rearm(rule_id()));

    let t = tracker.process_evaluation(rule_id(), cfg(0), true, true, at(5));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(t.excursion, Some(2));
}

#[test]
fn tracker_awaiting_rearm_opens_on_the_evaluation_whose_auto_resolve_is_false() {
    let mut tracker = ExcursionTracker::new();
    let config = cfg(0);
    let _ = tracker.process_evaluation(rule_id(), config, true, false, at(0));
    let _ = tracker.force_close(rule_id(), CloseReason::AutoResolve, at(2));
    assert!(tracker.awaiting_rearm(rule_id()));

    let t = tracker.process_evaluation(rule_id(), config, true, false, at(3));
    assert!(!tracker.awaiting_rearm(rule_id()));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(
        tracker.state(rule_id()).unwrap().state,
        TrackerStateKind::Active
    );
}

/// Low below 70, auto-resolved once glucose has held above 60 for 10 minutes.
fn relapsing_low_rule() -> Rule {
    Rule {
        id: rule_id(),
        condition_type: ConditionKind::Threshold,
        condition_params: json!({ "direction": "below", "value": 70 }),
        hysteresis_minutes: 0,
        auto_resolve_enabled: true,
        auto_resolve_params: Some(json!({
            "type": "sustained",
            "sustained": { "minutes": 10, "child": {
                "type": "threshold", "threshold": { "direction": "above", "value": 60 } } },
        })),
        on_unknown: OnUnknown::Hold,
        glucose_freshness: DEFAULT_GLUCOSE_FRESHNESS,
        enabled_at: None,
    }
}

fn reading_ctx(minutes: i64, mgdl: i64) -> SensorContext {
    SensorContext {
        latest_value: Some(Decimal::from(mgdl)),
        latest_timestamp: Some(at(minutes)),
        last_reading_at: Some(at(minutes)),
        ..Default::default()
    }
}

fn evaluation(rule: &Rule, state: &mut EngineState, minutes: i64, mgdl: i64) -> Evaluation {
    evaluate_rule(rule, &reading_ctx(minutes, mgdl), at(minutes), state, false)
        .evaluation
        .unwrap()
}

#[test]
fn an_auto_resolved_rule_reopens_when_its_resolve_tree_goes_false_while_its_body_holds() {
    let rule = relapsing_low_rule();
    let mut state = EngineState::new();
    assert_eq!(
        evaluation(&rule, &mut state, 0, 65).transition.kind,
        TransitionType::ExcursionOpened
    );
    let _ = evaluation(&rule, &mut state, 5, 65);
    let resolved = evaluation(&rule, &mut state, 10, 65);
    assert!(resolved.auto_resolved);
    assert!(resolved.tracker.unwrap().awaiting_rearm);

    let held = evaluation(&rule, &mut state, 15, 66);
    assert_eq!(
        held.transition.kind,
        TransitionType::None,
        "resolve still holds"
    );
    assert!(held.tracker.unwrap().awaiting_rearm);

    let relapse = evaluation(&rule, &mut state, 20, 45);
    assert_eq!(relapse.transition.kind, TransitionType::ExcursionOpened);
    assert!(!relapse.auto_resolved);
    assert!(!relapse.tracker.unwrap().awaiting_rearm);
}

#[test]
fn an_awaiting_rule_keeps_its_resolve_timer_running_so_a_standing_resolve_never_reopens() {
    let rule = relapsing_low_rule();
    let mut state = EngineState::new();
    for minute in [0, 5] {
        let _ = evaluation(&rule, &mut state, minute, 65);
    }
    assert!(evaluation(&rule, &mut state, 10, 65).auto_resolved);
    for minute in (15..=120).step_by(5) {
        let e = evaluation(&rule, &mut state, minute, 65);
        assert_eq!(e.transition.kind, TransitionType::None, "at {minute}");
        assert!(e.timer_ops.is_empty(), "at {minute}: {:?}", e.timer_ops);
    }
}

#[test]
fn tracker_closes_other_than_auto_resolve_of_an_active_excursion_stay_armed() {
    let closes: [(bool, CloseReason); 3] = [
        (true, CloseReason::Manual),
        (true, CloseReason::Hysteresis),
        (false, CloseReason::AutoResolve),
    ];
    for (active, reason) in closes {
        let mut tracker = ExcursionTracker::new();
        let _ = tracker.process_evaluation(rule_id(), cfg(60), true, false, at(0));
        if !active {
            let _ = tracker.process_evaluation(rule_id(), cfg(60), false, false, at(1));
        }
        let _ = tracker.force_close(rule_id(), reason, at(2));
        assert!(
            !tracker.state(rule_id()).unwrap().awaiting_rearm,
            "{reason:?}"
        );
        let t = tracker.process_evaluation(rule_id(), cfg(60), true, false, at(3));
        assert_eq!(t.kind, TransitionType::ExcursionOpened, "{reason:?}");
    }
}

#[test]
fn tracker_force_close_from_idle_is_noop() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(0), false, false, at(0));
    let t = tracker.force_close(rule_id(), CloseReason::Manual, at(5));
    assert_eq!(t.kind, TransitionType::None);
}

#[test]
fn tracker_get_active_excursion_id_by_state() {
    let mut tracker = ExcursionTracker::new();
    // Idle: none.
    let _ = tracker.process_evaluation(rule_id(), cfg(60), false, false, at(0));
    assert_eq!(tracker.active_excursion_id(rule_id()), None);
    // Active: some.
    let _ = tracker.process_evaluation(rule_id(), cfg(60), true, false, at(5));
    assert_eq!(tracker.active_excursion_id(rule_id()), Some(1));
    // Hysteresis: some.
    let _ = tracker.process_evaluation(rule_id(), cfg(60), false, false, at(10));
    assert_eq!(tracker.active_excursion_id(rule_id()), Some(1));
}

#[test]
fn tracker_reopen_after_close_creates_fresh_excursion_ordinal() {
    let mut tracker = ExcursionTracker::new();
    let _ = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(0));
    let _ = tracker.process_evaluation(rule_id(), cfg(0), false, false, at(5));
    let _ = tracker.process_evaluation(rule_id(), cfg(0), false, false, at(10));
    let t = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(15));
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
    recorded_tracker_ctx(reference_at, None)
}

fn recorded_tracker_ctx(
    reference_at: DateTime<Utc>,
    recorded_at: Option<DateTime<Utc>>,
) -> SensorContext {
    recorded_ctx(reference_at, recorded_at, false)
}

fn recorded_ctx(
    reference_at: DateTime<Utc>,
    recorded_at: Option<DateTime<Utc>>,
    scheduled: bool,
) -> SensorContext {
    let mut ctx = SensorContext::default();
    ctx.active_trackers.insert(
        tracker_definition(),
        TrackerReference {
            reference_at,
            recorded_at,
            scheduled,
        },
    );
    ctx
}

fn tracker_age_holds(ctx: &SensorContext, operator: &str, minutes: i32) -> Truth {
    eval_payload(
        ConditionKind::TrackerAge,
        &tracker_age_payload(operator, minutes),
        ctx,
    )
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
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", 48 * 60),
            &ctx
        )
    );
}

#[test]
fn tracker_age_false_before_threshold() {
    let ctx = tracker_ctx(base() - TimeDelta::hours(47));
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", 48 * 60),
            &ctx
        )
    );
}

#[test]
fn tracker_age_no_active_instance_is_false() {
    // Deliberately opposite to time_since_last_* cold-start infinity.
    let ctx = SensorContext::default();
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", 0),
            &ctx
        )
    );
}

#[test]
fn tracker_age_other_definition_is_false() {
    let mut ctx = SensorContext::default();
    ctx.active_trackers.insert(
        Uuid::from_u128(0xFFFF),
        TrackerReference::at(base() - TimeDelta::hours(48)),
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", 60),
            &ctx
        )
    );
}

/// A duration instance's start 12h ahead is an anchor beyond the clock
/// tolerance (§5.4): unknown, whatever the threshold.
#[test]
fn tracker_age_duration_start_ahead_of_the_clock_is_unknown() {
    let ctx = tracker_ctx(base() + TimeDelta::hours(12));
    assert_eq!(
        Unknown,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", -1440),
            &ctx
        )
    );
}

#[test]
fn tracker_age_negative_minutes_pre_event_window() {
    // Event tracker scheduled 12h from now: elapsed is -720 minutes.
    // A "-24h before event" threshold (minutes = -1440) is already crossed;
    // a "-6h before event" threshold (minutes = -360) is not yet.
    let ctx = recorded_ctx(base() + TimeDelta::hours(12), None, true);
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", -1440),
            &ctx
        )
    );
    assert_eq!(
        False,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", -360),
            &ctx
        )
    );
}

#[test]
fn tracker_age_threshold_due_before_recording_does_not_fire() {
    // Backdated 72h and recorded an hour ago: the 48h threshold was due 23h before.
    let ctx = recorded_tracker_ctx(
        base() - TimeDelta::hours(72),
        Some(base() - TimeDelta::hours(1)),
    );
    assert_eq!(False, tracker_age_holds(&ctx, ">=", 48 * 60));
    assert_eq!(False, tracker_age_holds(&ctx, ">", 48 * 60));
}

#[test]
fn tracker_age_threshold_due_after_recording_fires() {
    // Recorded 42h into the run: the 48h threshold fell due after.
    let ctx = recorded_tracker_ctx(
        base() - TimeDelta::hours(72),
        Some(base() - TimeDelta::hours(30)),
    );
    assert_eq!(True, tracker_age_holds(&ctx, ">=", 48 * 60));
}

#[test]
fn tracker_age_threshold_due_exactly_at_recording_does_not_fire() {
    let start = base() - TimeDelta::hours(72);
    let at_due = recorded_tracker_ctx(start, Some(start + TimeDelta::hours(48)));
    assert_eq!(False, tracker_age_holds(&at_due, ">=", 48 * 60));

    let just_before = recorded_tracker_ctx(
        start,
        Some(start + TimeDelta::hours(48) - TimeDelta::seconds(1)),
    );
    assert_eq!(True, tracker_age_holds(&just_before, ">=", 48 * 60));
}

#[test]
fn tracker_age_strictly_greater_is_held_back_only_after_its_due_time() {
    // `> 48h` does not hold at 48h, so a run recorded exactly then still alerts.
    let start = base() - TimeDelta::hours(72);
    let at_due = recorded_tracker_ctx(start, Some(start + TimeDelta::hours(48)));
    assert_eq!(True, tracker_age_holds(&at_due, ">", 48 * 60));

    let after = recorded_tracker_ctx(
        start,
        Some(start + TimeDelta::hours(48) + TimeDelta::seconds(1)),
    );
    assert_eq!(False, tracker_age_holds(&after, ">", 48 * 60));
}

#[test]
fn tracker_age_event_run_is_never_held_back() {
    // Booked two hours ago for 22h from now: the "24h before" step was due
    // before the booking, and still fires.
    let ctx = recorded_ctx(
        base() + TimeDelta::hours(22),
        Some(base() - TimeDelta::hours(2)),
        true,
    );
    assert_eq!(True, tracker_age_holds(&ctx, ">=", -24 * 60));
}

#[test]
fn tracker_age_later_threshold_of_the_same_run_still_fires() {
    // Recorded 50h into the run: 48h is held back, 70h fires once reached.
    let start = base() - TimeDelta::hours(72);
    let ctx = recorded_tracker_ctx(start, Some(start + TimeDelta::hours(50)));
    assert_eq!(False, tracker_age_holds(&ctx, ">=", 48 * 60));
    assert_eq!(True, tracker_age_holds(&ctx, ">=", 70 * 60));
}

#[test]
fn tracker_age_upper_bound_is_not_held_back_by_recording() {
    let ctx = recorded_tracker_ctx(base() - TimeDelta::hours(2), Some(base()));
    assert_eq!(True, tracker_age_holds(&ctx, "<", 3 * 60));
}

#[test]
fn tracker_age_wire_recorded_at_holds_back_a_due_threshold() {
    let wire = json!({
        "active_trackers": [
            {
                "tracker_definition_id": tracker_definition().to_string(),
                "reference_at": "2026-01-02T12:00:00Z",
                "recorded_at": "2026-01-05T11:00:00Z",
            }
        ]
    });
    let ctx: SensorContext = serde_json::from_value(wire).expect("context parses");
    assert_eq!(False, tracker_age_holds(&ctx, ">=", 48 * 60));
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
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::TrackerAge,
            &tracker_age_payload(">=", 48 * 60),
            &ctx
        )
    );
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
fn temp_basal_percent_division_overflow_is_unknown() {
    let ctx = temp_basal_ctx(Decimal::MAX, d("0.5"));
    assert_eq!(
        Unknown,
        eval_payload(
            ConditionKind::TempBasal,
            &json!({"metric": "percent_of_scheduled", "operator": ">=", "value": 0}),
            &ctx
        )
    );
}

#[test]
fn temp_basal_percent_multiplication_overflow_is_unknown() {
    let ctx = temp_basal_ctx(Decimal::MAX, d("2"));
    assert_eq!(
        Unknown,
        eval_payload(
            ConditionKind::TempBasal,
            &json!({"metric": "percent_of_scheduled", "operator": ">=", "value": 0}),
            &ctx
        )
    );
}

#[test]
fn temp_basal_percent_in_range_still_compares() {
    let ctx = temp_basal_ctx(d("1.5"), d("1"));
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::TempBasal,
            &json!({"metric": "percent_of_scheduled", "operator": "==", "value": 150}),
            &ctx
        )
    );
}

#[test]
fn tracker_numbers_each_rules_excursions_from_one() {
    let mut tracker = ExcursionTracker::new();
    let first = Uuid::from_u128(1);
    let second = Uuid::from_u128(2);
    let t = tracker.process_evaluation(first, cfg(0), true, false, at(0));
    assert_eq!(t.excursion, Some(1));
    let t = tracker.process_evaluation(second, cfg(0), true, false, at(0));
    assert_eq!(
        t.excursion,
        Some(1),
        "another rule's excursion does not move this rule's count"
    );
    let _ = tracker.force_close(first, CloseReason::Manual, at(1));
    let t = tracker.process_evaluation(first, cfg(0), true, false, at(2));
    assert_eq!(t.kind, TransitionType::ExcursionOpened);
    assert_eq!(t.excursion, Some(2));
    assert_eq!(tracker.state(first).unwrap().last_excursion, 2);
}

#[test]
fn a_restored_last_ordinal_below_the_active_one_is_raised_to_it() {
    let mut tracker = ExcursionTracker::new();
    tracker.restore_state(
        rule_id(),
        nocturne_alerts_core::excursion::TrackerState {
            state: TrackerStateKind::Active,
            active_excursion: Some(3),
            last_excursion: 0,
            updated_at: at(0),
            hysteresis_started_at: None,
            awaiting_rearm: false,
            held_since: None,
        },
    );
    let _ = tracker.force_close(rule_id(), CloseReason::Manual, at(1));
    let t = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(2));
    assert_eq!(t.excursion, Some(4));
}

#[test]
fn tracker_excursion_ordinal_saturates() {
    let mut tracker = ExcursionTracker::new();
    tracker.restore_state(
        rule_id(),
        nocturne_alerts_core::excursion::TrackerState {
            state: TrackerStateKind::Idle,
            active_excursion: None,
            last_excursion: u32::MAX,
            updated_at: at(0),
            hysteresis_started_at: None,
            awaiting_rearm: false,
            held_since: None,
        },
    );
    let t = tracker.process_evaluation(rule_id(), cfg(0), true, false, at(1));
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
    assert_eq!(
        True,
        eval_payload_at(
            ConditionKind::Staleness,
            &json!({"operator": ">", "value": 15}),
            &ctx,
            now
        )
    );
}

#[test]
fn a_hysteresis_close_leaves_the_rule_armed() {
    let restored = || TrackerState {
        state: TrackerStateKind::Hysteresis,
        active_excursion: Some(1),
        last_excursion: 1,
        updated_at: at(0),
        hysteresis_started_at: Some(at(0)),
        awaiting_rearm: true,
        held_since: None,
    };
    let mut by_evaluation = ExcursionTracker::new();
    by_evaluation.restore_state(rule_id(), restored());
    let t = by_evaluation.process_evaluation(rule_id(), cfg(5), false, true, at(10));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert!(!by_evaluation.state(rule_id()).unwrap().awaiting_rearm);

    let mut by_sweep = ExcursionTracker::new();
    by_sweep.restore_state(rule_id(), restored());
    let t = by_sweep.close_elapsed_hysteresis(rule_id(), cfg(5), at(10));
    assert_eq!(t.kind, TransitionType::ExcursionClosed);
    assert!(!by_sweep.state(rule_id()).unwrap().awaiting_rearm);
}

#[test]
fn hysteresis_expiry_past_the_calendar_does_not_panic() {
    let mut tracker = ExcursionTracker::new();
    tracker.restore_state(
        rule_id(),
        nocturne_alerts_core::excursion::TrackerState {
            state: TrackerStateKind::Hysteresis,
            active_excursion: Some(1),
            last_excursion: 1,
            updated_at: DateTime::<Utc>::MAX_UTC,
            hysteresis_started_at: None,
            awaiting_rearm: false,
            held_since: None,
        },
    );
    let t = tracker.process_evaluation(
        rule_id(),
        cfg(i32::MAX),
        false,
        false,
        DateTime::<Utc>::MAX_UTC,
    );
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
        "active_pump_modes": [{ "mode": "Turbo", "started_at": "2026-01-05T11:00:00Z" }],
        "active_state_spans": [
            { "category": "NotACategory", "state": null, "started_at": "2026-01-05T11:00:00Z" },
            { "category": "Illness", "state": null, "started_at": "2026-01-05T11:00:00Z" },
        ],
    });
    let ctx: SensorContext = serde_json::from_value(wire).expect("context still parses");
    assert_eq!(ctx.trend_bucket, None);
    assert_eq!(ctx.glucose_bucket, None);
    assert_eq!(
        ctx.active_pump_modes
            .iter()
            .map(|m| m.mode)
            .collect::<Vec<_>>(),
        [None]
    );
    assert_eq!(ctx.active_state_spans.len(), 1);

    assert_eq!(
        True,
        eval_payload(
            ConditionKind::Threshold,
            &json!({"direction": "below", "value": 70}),
            &ctx
        )
    );
    assert_eq!(
        True,
        eval_payload(
            ConditionKind::StateSpanActive,
            &json!({"category": "Illness", "state": null, "is_active": true}),
            &ctx
        )
    );
    assert_eq!(
        Unknown,
        eval_payload(ConditionKind::Trend, &json!({"bucket": "flat"}), &ctx)
    );
}

fn context_with(fields: Value) -> SensorContext {
    serde_json::from_value(fields).expect("context parses")
}

/// Every active span has lasted 60 minutes at `base()`. Rows: configured mode
/// Automatic; columns: `is_active`, `for_minutes` (none, 30 held, 90 not yet).
#[test]
fn pump_state_over_every_set_of_active_modes_reads_the_spec_table() {
    let cells = |active: &[&str]| {
        let modes: Vec<Value> = active
            .iter()
            .map(|mode| json!({ "mode": mode, "started_at": "2026-01-05T11:00:00Z" }))
            .collect();
        let ctx = context_with(json!({ "active_pump_modes": modes }));
        let mut read = Vec::new();
        for is_active in [true, false] {
            for for_minutes in [Value::Null, json!(30), json!(90)] {
                let payload = json!({
                    "mode": "Automatic", "is_active": is_active, "for_minutes": for_minutes
                });
                read.push(eval_payload(ConditionKind::PumpState, &payload, &ctx));
            }
        }
        read
    };
    let (t, f, u) = (True, False, Unknown);
    let none = [f, f, f, t, t, t];
    let named = [t, t, f, f, f, f];
    let unrecognised = [u, u, f, u, u, u];
    assert_eq!(cells(&[]), none, "no active state");
    assert_eq!(cells(&["Automatic"]), named, "the configured mode");
    assert_eq!(cells(&["Manual"]), none, "another known mode");
    assert_eq!(cells(&["Turbo"]), unrecognised, "an unrecognised mode");
    assert_eq!(
        cells(&["Manual", "Automatic"]),
        named,
        "the configured mode beside another"
    );
    assert_eq!(
        cells(&["Suspended", "Manual"]),
        none,
        "other known modes only"
    );
    assert_eq!(
        cells(&["Manual", "Turbo"]),
        unrecognised,
        "an unrecognised mode beside another known mode"
    );
    assert_eq!(
        cells(&["Turbo", "Automatic"]),
        named,
        "the configured mode decides beside an unrecognised one"
    );
    assert_eq!(
        cells(&["Turbo", "Manual", "Automatic"]),
        named,
        "the configured mode decides beside both"
    );
}

#[test]
fn a_known_pump_mode_still_answers_both_sides() {
    let ctx = context_with(json!({
        "active_pump_modes": [{ "mode": "Manual", "started_at": "2026-01-05T11:00:00Z" }],
    }));
    let at = |mode, is_active| {
        eval_payload(
            ConditionKind::PumpState,
            &json!({ "mode": mode, "is_active": is_active }),
            &ctx,
        )
    };
    assert_eq!(True, at("Manual", true));
    assert_eq!(False, at("Manual", false));
    assert_eq!(False, at("Automatic", true));
    assert_eq!(True, at("Automatic", false));
}

fn pump_state_leaf(mode: &str, is_active: bool, for_minutes: Option<i32>) -> Value {
    leaf(
        "pump_state",
        json!({ "mode": mode, "is_active": is_active, "for_minutes": for_minutes }),
    )
}

#[test]
fn two_pump_state_leaves_for_different_modes_in_one_tree_each_read_their_own() {
    let ctx = context_with(json!({
        "active_pump_modes": [
            { "mode": "Automatic", "started_at": ago(60) },
            { "mode": "Suspended", "started_at": ago(5) },
        ],
    }));
    let both = |first: Value, second: Value| {
        eval_tree(
            &json!({ "type": "composite", "composite": {
                "operator": "and", "conditions": [first, second] } }),
            &ctx,
        )
    };
    assert_eq!(
        True,
        both(
            pump_state_leaf("Suspended", true, None),
            pump_state_leaf("Automatic", true, None),
        )
    );
    assert_eq!(
        True,
        both(
            pump_state_leaf("Suspended", true, Some(5)),
            pump_state_leaf("Automatic", true, Some(30)),
        ),
        "each leaf holds for its own span"
    );
    assert_eq!(
        False,
        both(
            pump_state_leaf("Suspended", true, Some(10)),
            pump_state_leaf("Automatic", true, Some(30)),
        ),
        "the suspension has not lasted 10 minutes, whatever the automatic span has lasted"
    );
    assert_eq!(
        True,
        both(
            pump_state_leaf("Manual", false, None),
            pump_state_leaf("Suspended", true, None),
        ),
        "a mode that is not active is absent whatever is"
    );
}

#[test]
fn a_repeated_pump_mode_reads_the_later_start_whatever_the_order() {
    let leaf_for = |starts: &[String; 2], for_minutes| {
        let ctx = context_with(json!({
            "active_pump_modes": [
                { "mode": "Automatic", "started_at": starts[0] },
                { "mode": "Automatic", "started_at": starts[1] },
            ],
        }));
        eval_payload(
            ConditionKind::PumpState,
            &json!({ "mode": "Automatic", "is_active": true, "for_minutes": for_minutes }),
            &ctx,
        )
    };
    for starts in [[ago(60), ago(10)], [ago(10), ago(60)]] {
        assert_eq!(False, leaf_for(&starts, 30), "{starts:?}");
        assert_eq!(True, leaf_for(&starts, 5), "{starts:?}");
    }
}

#[test]
fn unrecognised_pump_modes_are_unknown_from_the_earliest_start() {
    let leaf_for = |started: &[String], for_minutes| {
        let modes: Vec<Value> = started
            .iter()
            .map(|at| json!({ "mode": "Turbo", "started_at": at }))
            .collect();
        eval_payload(
            ConditionKind::PumpState,
            &json!({ "mode": "Automatic", "is_active": true, "for_minutes": for_minutes }),
            &context_with(json!({ "active_pump_modes": modes })),
        )
    };
    assert_eq!(Unknown, leaf_for(&[ago(60), ago(10)], 30));
    assert_eq!(Unknown, leaf_for(&[ago(10), ago(60)], 30));
    assert_eq!(False, leaf_for(&[ago(10), ago(20)], 30));
}

#[test]
fn a_pump_mode_entry_for_another_mode_ahead_of_the_clock_is_not_read() {
    let ctx = context_with(json!({
        "active_pump_modes": [
            { "mode": "Manual", "started_at": ago(-60) },
            { "mode": "Automatic", "started_at": ago(40) },
        ],
    }));
    let at = |mode, for_minutes| {
        eval_payload(
            ConditionKind::PumpState,
            &json!({ "mode": mode, "is_active": true, "for_minutes": for_minutes }),
            &ctx,
        )
    };
    assert_eq!(True, at("Automatic", 30));
    assert_eq!(Unknown, at("Manual", 5), "its own start is not known");
}

#[test]
fn a_state_span_of_a_category_the_crate_cannot_name_matches_no_leaf() {
    let with = context_with(json!({
        "active_state_spans": [
            { "category": "NotACategory", "state": null, "started_at": "2026-01-05T11:00:00Z" },
            { "category": "NotACategory", "state": "x", "started_at": "2026-01-05T11:00:00Z" },
        ],
    }));
    let without = context_with(json!({}));
    for category in StateSpanCategory::ALL
        .iter()
        .filter(|c| **c != StateSpanCategory::PumpMode)
    {
        for state in [Value::Null, json!("x")] {
            for is_active in [true, false] {
                let payload = json!({
                    "category": category.name(), "state": state, "is_active": is_active
                });
                assert_eq!(
                    eval_payload(ConditionKind::StateSpanActive, &payload, &without),
                    eval_payload(ConditionKind::StateSpanActive, &payload, &with),
                    "{} {state} active={is_active}",
                    category.name()
                );
            }
        }
    }
}

#[test]
fn a_bucket_name_the_crate_cannot_name_reads_as_an_absent_bucket() {
    let unrecognised = context_with(json!({
        "latest_value": 55,
        "trend_bucket": "sideways",
        "glucose_bucket": "off_the_chart",
    }));
    let absent = context_with(json!({ "latest_value": 55 }));
    for bucket in TrendBucket::ALL {
        let payload = json!({ "bucket": bucket.name() });
        assert_eq!(
            Unknown,
            eval_payload(ConditionKind::Trend, &payload, &unrecognised)
        );
        assert_eq!(
            eval_payload(ConditionKind::Trend, &payload, &absent),
            eval_payload(ConditionKind::Trend, &payload, &unrecognised)
        );
    }
    for bucket in GlucoseBucket::ALL {
        let payload = json!({ "buckets": [bucket.name()] });
        assert_eq!(
            Unknown,
            eval_payload(ConditionKind::GlucoseBucket, &payload, &unrecognised)
        );
    }
    let negated = eval_tree(
        &json!({ "type": "not", "not": { "child": {
            "type": "trend", "trend": { "bucket": "flat" } } } }),
        &unrecognised,
    );
    assert_eq!(Unknown, negated);
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
fn a_repeated_property_binds_its_last_occurrence() {
    let ctx = glucose_ctx(Some("60"), Some("0"));
    let last_is_70 = raw(r#"{"direction": "below", "value": 50, "value": 70}"#);
    let last_is_50 = raw(r#"{"direction": "below", "value": 70, "value": 50}"#);
    assert_eq!(
        True,
        eval_payload(ConditionKind::Threshold, &last_is_70, &ctx)
    );
    assert_eq!(
        False,
        eval_payload(ConditionKind::Threshold, &last_is_50, &ctx)
    );
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
fn an_enum_value_is_exactly_a_wire_name() {
    let parses = |category: Value| {
        parse_payload(
            ConditionKind::StateSpanActive,
            &json!({ "category": category, "is_active": true }),
        )
        .is_ok()
    };
    assert!(parses(json!("Exercise")));
    for other in [
        json!(4),
        json!("4"),
        json!(" 4"),
        json!("exercise"),
        json!("Exercise "),
        json!(null),
    ] {
        assert!(!parses(other.clone()), "{other}");
    }
}

fn threshold_value(literal: &str) -> Option<String> {
    let payload = raw(&format!(r#"{{"direction": "below", "value": {literal}}}"#));
    match parse_payload(ConditionKind::Threshold, &payload).ok()? {
        nocturne_alerts_core::model::Payload::Threshold(t) => match t.value {
            nocturne_alerts_core::model::Limit::Literal(value) => Some(value.to_string()),
            nocturne_alerts_core::model::Limit::Fact(_) => None,
        },
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

fn too_deep<T>(read: ParseResult<T>) -> bool {
    read.err()
        .is_some_and(|e| e.0.iter().any(|issue| issue.reason == Reason::TooDeep))
}

#[test]
fn node_depth_limit_counts_typed_values_like_system_text_json() {
    // 30 not levels (60 objects) + threshold node and payload = 62.
    assert!(Node::parse(&not_chain(30, threshold_node())).is_ok());
    // 63 typed objects is the most STJ builds.
    assert!(!too_deep(Node::parse(&not_chain(
        31,
        json!({ "type": "threshold" })
    ))));
    assert!(too_deep(Node::parse(&not_chain(31, threshold_node()))));
    // A condition list is a typed frame too: 60 + node + payload + list + node = 64.
    let composite = json!({
        "type": "composite",
        "composite": { "operator": "and", "conditions": [{ "type": "threshold" }] }
    });
    assert!(too_deep(Node::parse(&not_chain(30, composite))));
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
    assert!(!too_deep(Node::parse(&not_chain(30, with_arrays(2)))));
    assert!(too_deep(Node::parse(&not_chain(30, with_arrays(3)))));
}

#[test]
fn payload_depth_counts_from_the_payload_document() {
    let payload = |levels| json!({ "child": not_chain(levels, json!({ "type": "threshold", "threshold": {} })) });
    // payload + 60 + node + payload = 63.
    assert!(!too_deep(parse_payload(ConditionKind::Not, &payload(30))));
    assert!(too_deep(parse_payload(ConditionKind::Not, &payload(31))));
}

#[test]
fn very_deep_nodes_are_a_parse_error_not_a_stack_overflow() {
    assert!(Node::parse(&not_chain(500, threshold_node())).is_err());
}

// ---------------------------------------------------------------------------
// sustained: the instant a child has held since (engine-semantics.md §3)
// ---------------------------------------------------------------------------

/// The start a `sustained{minutes: 600}` over `child` sets on its first true
/// evaluation at `base()`, its path's last not-true evaluation (and a nested
/// `sustained` child's) a day before.
fn start_over(child: Value, ctx: Value) -> DateTime<Utc> {
    let tree = json!({"type": "sustained", "sustained": {"minutes": 600, "child": child}});
    let node = Node::parse(&tree).expect("node parses");
    let ctx: SensorContext = serde_json::from_value(ctx).expect("a context");
    let mut timers = TimerStore::new();
    for path in ["sustained", "sustained[0].sustained"] {
        timers.seed(
            Uuid::nil(),
            path,
            TimerState::NotTrue(base() - TimeDelta::days(1)),
        );
    }
    let mut env = Env::new(base(), Uuid::nil(), &ctx, &mut timers);
    assert_eq!(False, eval_node(&node, "sustained", &mut env), "{tree}");
    match timers.snapshot_for_rule(Uuid::nil()).next() {
        Some(("sustained", TimerState::Running(start))) => start,
        other => panic!("{tree}: no run started ({other:?})"),
    }
}

fn leaf(kind: &str, payload: Value) -> Value {
    json!({ "type": kind, kind: payload })
}

fn ago(minutes: i64) -> String {
    (base() - TimeDelta::minutes(minutes)).to_rfc3339()
}

#[test]
fn each_leaf_reports_since_when_it_has_held_as_the_spec_table_says() {
    let minutes = |m: i64| base() - TimeDelta::minutes(m);
    let tick = TimeDelta::nanoseconds(100);
    let reading = json!({
        "latest_value": 60, "trend_rate": -2, "trend_bucket": "falling",
        "glucose_bucket": "low", "latest_timestamp": ago(3), "latest_received_at": ago(1),
        "last_reading_at": ago(3), "predictions": [{"offset_minutes": 10, "mgdl": 60}],
    });
    let stopped = json!({"last_reading_at": ago(25), "latest_timestamp": ago(25)});
    let pump = |extra: Value| {
        let mut ctx = json!({ "has_ever_pump_snapshot": true });
        ctx.as_object_mut()
            .unwrap()
            .extend(extra.as_object().unwrap().clone());
        ctx
    };
    let suspended = pump(json!({"active_pump_suspension": {"started_at": ago(40)}}));
    let overridden = json!({"active_override": {"started_at": ago(40)}});
    let temp =
        json!({"active_temp_basal": {"rate": 1, "scheduled_rate": 0.8, "started_at": ago(20)}});
    let tracker = Uuid::from_u128(7);
    let other_rule = Uuid::from_u128(8);
    let below = || leaf("threshold", json!({"direction": "below", "value": 70}));
    let override_on = || leaf("override_active", json!({"is_active": true}));
    let cases: Vec<(&str, Value, Value, DateTime<Utc>)> = vec![
        ("threshold", below(), reading.clone(), minutes(3)),
        (
            "rate_of_change",
            leaf("rate_of_change", json!({"direction": "falling", "rate": 1})),
            reading.clone(),
            minutes(3),
        ),
        (
            "trend",
            leaf("trend", json!({"bucket": "falling"})),
            reading.clone(),
            minutes(3),
        ),
        (
            "glucose_bucket",
            leaf("glucose_bucket", json!({"buckets": ["low"]})),
            reading.clone(),
            minutes(3),
        ),
        (
            "predicted",
            leaf(
                "predicted",
                json!({"operator": "<", "value": 70, "within_minutes": 30}),
            ),
            reading.clone(),
            minutes(3),
        ),
        (
            "staleness",
            leaf("staleness", json!({"operator": ">=", "value": 10})),
            stopped.clone(),
            minutes(15),
        ),
        (
            "staleness below its bound knows none",
            leaf("staleness", json!({"operator": "<", "value": 30})),
            stopped.clone(),
            base(),
        ),
        (
            "signal_loss",
            leaf("signal_loss", json!({"timeout_minutes": 10})),
            stopped.clone(),
            minutes(15),
        ),
        (
            "site_age",
            leaf("site_age", json!({"operator": ">=", "value": 2})),
            json!({"last_site_change_at": ago(180)}),
            minutes(60),
        ),
        (
            "sensor_age",
            leaf("sensor_age", json!({"operator": ">=", "value": 1})),
            json!({"last_sensor_start_at": ago(30 * 60)}),
            minutes(6 * 60),
        ),
        (
            "loop_stale",
            leaf("loop_stale", json!({"operator": ">=", "minutes": 15})),
            json!({"has_ever_aps_cycled": true, "last_aps_cycle_at": ago(20)}),
            minutes(5),
        ),
        (
            "loop_stale, strictly past its bound",
            leaf("loop_stale", json!({"operator": ">", "minutes": 15})),
            json!({"has_ever_aps_cycled": true, "last_aps_cycle_at": ago(20)}),
            minutes(5) + tick,
        ),
        (
            "loop_enaction_stale",
            leaf(
                "loop_enaction_stale",
                json!({"operator": ">=", "minutes": 15}),
            ),
            json!({"has_ever_aps_cycled": true, "last_aps_enacted_at": ago(20)}),
            minutes(5),
        ),
        (
            "time_since_last_carb",
            leaf(
                "time_since_last_carb",
                json!({"operator": ">=", "minutes": 30}),
            ),
            json!({"last_carb_at": ago(45)}),
            minutes(15),
        ),
        (
            "time_since_last_bolus",
            leaf(
                "time_since_last_bolus",
                json!({"operator": ">", "minutes": 30}),
            ),
            json!({"last_bolus_at": ago(45)}),
            minutes(15) + tick,
        ),
        (
            "tracker_age",
            leaf(
                "tracker_age",
                json!({"tracker_definition_id": tracker, "operator": ">=", "minutes": 60}),
            ),
            json!({"active_trackers": [
                {"tracker_definition_id": tracker, "reference_at": ago(90), "recorded_at": ago(90)}
            ]}),
            minutes(30),
        ),
        (
            "pump_suspended",
            leaf("pump_suspended", json!({"is_active": true})),
            suspended.clone(),
            minutes(40),
        ),
        (
            "pump_suspended with for_minutes",
            leaf(
                "pump_suspended",
                json!({"is_active": true, "for_minutes": 10}),
            ),
            suspended.clone(),
            minutes(30),
        ),
        (
            "pump_suspended, inactive side",
            leaf("pump_suspended", json!({"is_active": false})),
            pump(json!({})),
            base(),
        ),
        (
            "override_active",
            override_on(),
            overridden.clone(),
            minutes(40),
        ),
        (
            "do_not_disturb",
            leaf("do_not_disturb", json!({"is_active": true})),
            json!({"active_do_not_disturb": {"started_at": ago(40)}}),
            minutes(40),
        ),
        (
            "pump_state",
            leaf(
                "pump_state",
                json!({"mode": "Automatic", "is_active": true}),
            ),
            json!({"active_pump_modes": [{"mode": "Automatic", "started_at": ago(40)}]}),
            minutes(40),
        ),
        (
            "pump_state, inactive side",
            leaf("pump_state", json!({"mode": "Manual", "is_active": false})),
            json!({"active_pump_modes": [{"mode": "Automatic", "started_at": ago(40)}]}),
            base(),
        ),
        (
            "state_span_active",
            leaf(
                "state_span_active",
                json!({"category": "Override", "is_active": true}),
            ),
            json!({"active_state_spans": [{"category": "Override", "started_at": ago(40)}]}),
            minutes(40),
        ),
        (
            "temp_basal rate",
            leaf(
                "temp_basal",
                json!({"metric": "rate", "operator": ">", "value": 0.5}),
            ),
            temp.clone(),
            minutes(20),
        ),
        (
            "temp_basal percent_of_scheduled",
            leaf(
                "temp_basal",
                json!({"metric": "percent_of_scheduled", "operator": ">", "value": 100}),
            ),
            temp.clone(),
            base(),
        ),
        (
            "alert_state",
            leaf(
                "alert_state",
                json!({"alert_id": other_rule, "state": "firing", "for_minutes": 5}),
            ),
            json!({"active_alerts": [
                {"alert_id": other_rule, "state": "firing", "triggered_at": ago(40)}
            ]}),
            base(),
        ),
        (
            "iob",
            leaf("iob", json!({"operator": ">", "value": 1})),
            json!({"iob_units": 2}),
            base(),
        ),
        (
            "cob",
            leaf("cob", json!({"operator": ">", "value": 1})),
            json!({"cob_grams": 2}),
            base(),
        ),
        (
            "reservoir",
            leaf("reservoir", json!({"operator": "<", "value": 10})),
            json!({"reservoir_units": 5}),
            base(),
        ),
        (
            "pump_battery",
            leaf("pump_battery", json!({"operator": "<", "value": 10})),
            pump(json!({"pump_battery_percent": 5})),
            base(),
        ),
        (
            "uploader_battery",
            leaf("uploader_battery", json!({"operator": "<", "value": 10})),
            json!({"has_ever_uploader_snapshot": true, "uploader_battery_percent": 5}),
            base(),
        ),
        (
            "transmitter_battery",
            leaf(
                "transmitter_battery",
                json!({"metric": "days", "operator": ">", "value": 10}),
            ),
            json!({"transmitter_days": 50}),
            base(),
        ),
        (
            "sensitivity_ratio",
            leaf("sensitivity_ratio", json!({"operator": ">", "value": 1})),
            json!({"has_ever_aps_sensitivity": true, "sensitivity_ratio": 1.5}),
            base(),
        ),
        (
            "time_of_day",
            leaf("time_of_day", json!({"from": "00:00", "to": "23:59"})),
            json!({}),
            base(),
        ),
        (
            "day_of_week",
            leaf("day_of_week", json!({"days": ["monday"]})),
            json!({}),
            base(),
        ),
        (
            "sleep_session_active",
            leaf("sleep_session_active", json!({"is_active": true})),
            json!({"sleep_session_active": true}),
            base(),
        ),
        (
            "and: the latest of its children",
            leaf(
                "composite",
                json!({"operator": "and", "conditions": [below(), override_on()]}),
            ),
            json!({"latest_value": 60, "latest_timestamp": ago(3), "active_override": {"started_at": ago(40)}}),
            minutes(3),
        ),
        (
            "and with a child that knows none",
            leaf(
                "composite",
                json!({"operator": "and", "conditions": [
                    override_on(), leaf("iob", json!({"operator": ">", "value": 1}))
                ]}),
            ),
            json!({"iob_units": 2, "active_override": {"started_at": ago(40)}}),
            base(),
        ),
        (
            "or: the earliest of its true children",
            leaf(
                "composite",
                json!({"operator": "or", "conditions": [
                    below(), override_on(), leaf("iob", json!({"operator": ">", "value": 1}))
                ]}),
            ),
            json!({
                "latest_value": 60, "latest_timestamp": ago(3), "iob_units": 2,
                "active_override": {"started_at": ago(40)}
            }),
            minutes(40),
        ),
        (
            "or: a false child is not read",
            leaf(
                "composite",
                json!({"operator": "or", "conditions": [below(), override_on()]}),
            ),
            json!({"latest_value": 90, "latest_timestamp": ago(50), "active_override": {"started_at": ago(40)}}),
            minutes(40),
        ),
        (
            "not",
            leaf(
                "not",
                json!({"child": leaf("override_active", json!({"is_active": false}))}),
            ),
            overridden.clone(),
            base(),
        ),
        (
            "a nested sustained: its start plus its minutes",
            leaf("sustained", json!({"minutes": 15, "child": override_on()})),
            overridden.clone(),
            minutes(25),
        ),
    ];
    for (label, child, ctx, expected) in cases {
        assert_eq!(start_over(child, ctx), expected, "{label}");
    }
}

#[test]
fn a_start_is_never_before_the_last_not_true_evaluation_nor_after_now() {
    let tree = json!({"type": "sustained", "sustained": {"minutes": 30, "child":
        {"type": "override_active", "override_active": {"is_active": true}}}});
    let node = Node::parse(&tree).expect("node parses");
    let at = |now: DateTime<Utc>, started: Option<DateTime<Utc>>, timers: &mut TimerStore| {
        let ctx: SensorContext = serde_json::from_value(match started {
            Some(at) => json!({"active_override": {"started_at": at}}),
            None => json!({}),
        })
        .unwrap();
        eval_node(
            &node,
            "sustained",
            &mut Env::new(now, Uuid::nil(), &ctx, timers),
        )
    };
    let state = |timers: &TimerStore| timers.snapshot_for_rule(Uuid::nil()).next().map(|(_, s)| s);
    let hour_before = Some(base() - TimeDelta::hours(1));

    // False at T0, then a span recorded as begun an hour before T0: the run
    // starts where T0's lease ends.
    let mut timers = TimerStore::new();
    assert_eq!(False, at(base(), None, &mut timers));
    assert_eq!(
        False,
        at(base() + TimeDelta::minutes(10), hour_before, &mut timers)
    );
    let lease_end = base() + NOT_TRUE_LEASE;
    assert_eq!(state(&timers), Some(TimerState::Running(lease_end)));
    assert_eq!(
        False,
        at(lease_end + TimeDelta::minutes(29), hour_before, &mut timers)
    );
    assert_eq!(
        True,
        at(lease_end + TimeDelta::minutes(30), hour_before, &mut timers)
    );

    // Inside the lease the bound is `now`.
    let mut timers = TimerStore::new();
    assert_eq!(False, at(base(), None, &mut timers));
    let inside = base() + TimeDelta::minutes(2);
    assert_eq!(False, at(inside, hour_before, &mut timers));
    assert_eq!(state(&timers), Some(TimerState::Running(inside)));

    // A span that claims to begin after now starts the run at now.
    let mut timers = TimerStore::new();
    let ahead = Some(base() + TimeDelta::minutes(3));
    assert_eq!(False, at(base(), ahead, &mut timers));
    assert_eq!(state(&timers), Some(TimerState::Running(base())));

    // No state and no enablement: the run starts now, however long the span ran.
    let mut timers = TimerStore::new();
    assert_eq!(False, at(base(), hour_before, &mut timers));
    assert_eq!(state(&timers), Some(TimerState::Running(base())));

    // A last not-true evaluation recorded after now, by a clock that went
    // back, does not move back, and a run then starts at now.
    let mut timers = TimerStore::new();
    let later = TimerState::NotTrue(base() + TimeDelta::minutes(10));
    timers.seed(Uuid::nil(), "sustained", later);
    assert_eq!(False, at(base(), None, &mut timers));
    assert_eq!(state(&timers), Some(later));
    assert_eq!(False, at(base(), hour_before, &mut timers));
    assert_eq!(state(&timers), Some(TimerState::Running(base())));
}

#[test]
fn a_rule_s_first_evaluation_bounds_a_start_by_its_enabled_at() {
    let rule_id = Uuid::from_u128(1);
    let rule = |enabled_at: Option<DateTime<Utc>>| {
        let wire: WireRule = serde_json::from_value(json!({
            "id": rule_id,
            "condition_type": "sustained",
            "condition_params": {"minutes": 30, "child":
                {"type": "override_active", "override_active": {"is_active": true}}},
            "enabled_at": enabled_at,
        }))
        .unwrap();
        Rule::try_from(wire).unwrap()
    };
    let ctx: SensorContext = serde_json::from_value(
        json!({"active_override": {"started_at": base() - TimeDelta::hours(2)}}),
    )
    .unwrap();
    let root = |rule: &Rule, state: &mut EngineState| {
        evaluate_rule(rule, &ctx, base(), state, false)
            .evaluation
            .unwrap()
            .root
    };
    let hour_ago = Some(base() - TimeDelta::hours(1));

    assert_eq!(True, root(&rule(hour_ago), &mut EngineState::new()));
    assert_eq!(
        False,
        root(
            &rule(Some(base() - TimeDelta::minutes(10))),
            &mut EngineState::new()
        )
    );
    assert_eq!(False, root(&rule(None), &mut EngineState::new()));
    // A rule with tracker state is not on its first evaluation.
    let mut evaluated = EngineState::new();
    let _ = evaluated.tracker.process_evaluation(
        rule_id,
        TrackerRuleConfig {
            hysteresis_minutes: 0,
        },
        false,
        false,
        base() - TimeDelta::minutes(1),
    );
    assert_eq!(False, root(&rule(hour_ago), &mut evaluated));
}

#[test]
fn a_not_true_instant_is_recorded_at_most_once_a_lease_and_whenever_a_run_ends() {
    let tree = json!({"type": "sustained", "sustained": {"minutes": 30, "child":
        {"type": "override_active", "override_active": {"is_active": true}}}});
    let node = Node::parse(&tree).expect("node parses");
    let mut timers = TimerStore::new();
    let mut at = |minutes: i64, on: bool| {
        let ctx: SensorContext = serde_json::from_value(if on {
            json!({"active_override": {"started_at": base() - TimeDelta::hours(1)}})
        } else {
            json!({})
        })
        .unwrap();
        let now = base() + TimeDelta::minutes(minutes);
        eval_node(
            &node,
            "sustained",
            &mut Env::new(now, Uuid::nil(), &ctx, &mut timers),
        );
        timers
            .drain_ops()
            .into_iter()
            .map(|op| (op.kind.wire(), (op.at - base()).num_minutes()))
            .collect::<Vec<_>>()
    };
    let lease = NOT_TRUE_LEASE.num_minutes();
    assert_eq!(
        at(0, false),
        vec![("clear", 0)],
        "a path with no state records"
    );
    assert_eq!(at(lease - 1, false), vec![], "inside the lease");
    assert_eq!(
        at(lease, false),
        vec![("clear", lease)],
        "the lease has run out"
    );
    assert_eq!(at(lease + 1, true), vec![("set", lease + 1)]);
    assert_eq!(
        at(lease + 2, false),
        vec![("clear", lease + 2)],
        "a run that ends records whatever the age of the last record"
    );
    assert_eq!(
        at(lease - 10, false),
        vec![],
        "a clock that went back moves nothing"
    );
}

/// The engine bounds a start only by the not-true instant it is given (engine-semantics.md §3,
/// §7). An override back-dated to 40 minutes ago, read at noon, under a `sustained{30}`: a
/// record of the false evaluation a minute ago starts the run at noon, but a record left from
/// yesterday, with the evaluations since never recorded, lets it start 40 minutes ago and hold at
/// once. So the host raises a stored instant to when it began recording without interruption.
#[test]
fn a_start_is_bounded_only_by_the_not_true_instant_the_host_gives() {
    let noon = base();
    let tree = json!({"type": "sustained", "sustained": {"minutes": 30, "child":
        {"type": "override_active", "override_active": {"is_active": true}}}});
    let node = Node::parse(&tree).expect("node parses");
    let ctx: SensorContext = serde_json::from_value(
        json!({"active_override": {"started_at": noon - TimeDelta::minutes(40)}}),
    )
    .unwrap();
    let from = |recorded: DateTime<Utc>| {
        let mut timers = TimerStore::new();
        timers.seed(Uuid::nil(), "sustained", TimerState::NotTrue(recorded));
        let root = eval_node(
            &node,
            "sustained",
            &mut Env::new(noon, Uuid::nil(), &ctx, &mut timers),
        );
        let start = match timers.snapshot_for_rule(Uuid::nil()).next() {
            Some((_, TimerState::Running(start))) => start,
            other => panic!("no run started ({other:?})"),
        };
        (root, start)
    };

    assert_eq!(from(noon - TimeDelta::minutes(1)), (False, noon));
    assert_eq!(
        from(noon - TimeDelta::days(1)),
        (True, noon - TimeDelta::minutes(40)),
        "a stale record is taken at its word"
    );
    assert_eq!(
        from(noon),
        (False, noon),
        "a record raised to noon starts the run there"
    );
}
