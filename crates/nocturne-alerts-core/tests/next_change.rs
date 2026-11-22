//! `next_change_at` (engine-semantics.md §5.3): never later than the first
//! instant an evaluation can differ, always after `now`, and the outcome is
//! constant on the open interval it bounds (by example here; the property is
//! in `properties.rs`).

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    clippy::as_conversions,
    reason = "test code"
)]

use chrono::{DateTime, TimeDelta, Utc};
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Evaluation, Rule, WireRule};
use nocturne_alerts_core::excursion::{HOLD_BOUND, TrackerState, TrackerStateKind};
use nocturne_alerts_core::next_change::RESOLUTION;
use nocturne_alerts_core::sustained::TimerState;

#[path = "common/quiet.rs"]
mod quiet;

const RULE: Uuid = Uuid::from_u128(1);
const OTHER: Uuid = Uuid::from_u128(2);
const TRACKER: Uuid = Uuid::from_u128(3);

fn at(s: &str) -> DateTime<Utc> {
    s.parse().unwrap()
}

fn now() -> DateTime<Utc> {
    at("2026-06-10T12:00:00Z")
}

fn ago(d: TimeDelta) -> String {
    (now() - d).to_rfc3339()
}

fn min(m: i64) -> TimeDelta {
    TimeDelta::minutes(m)
}

fn rule(kind: &str, params: Value) -> Rule {
    rule_with(kind, params, None, 0)
}

fn rule_with(kind: &str, params: Value, auto_resolve: Option<Value>, hysteresis: i32) -> Rule {
    let wire: WireRule = serde_json::from_value(json!({
        "id": RULE,
        "condition_type": kind,
        "condition_params": params,
        "hysteresis_minutes": hysteresis,
        "auto_resolve_enabled": auto_resolve.is_some(),
        "auto_resolve_params": auto_resolve,
    }))
    .unwrap();
    Rule::try_from(wire).unwrap()
}

fn ctx(v: Value) -> SensorContext {
    serde_json::from_value(v).unwrap()
}

/// State seeded before the evaluation at `now`.
#[derive(Clone, Default)]
struct Seed {
    timers: Vec<(&'static str, TimerState)>,
    tracker: Option<TrackerState>,
}

impl Seed {
    fn state(&self) -> EngineState {
        let mut state = EngineState::new();
        for &(path, timer) in &self.timers {
            state.timers.seed(RULE, path, timer);
        }
        if let Some(tracker) = self.tracker {
            state.tracker.restore_state(RULE, tracker);
        }
        state
    }
}

fn tracker(kind: TrackerStateKind, hysteresis_started_at: Option<DateTime<Utc>>) -> TrackerState {
    TrackerState {
        state: kind,
        active_excursion: (kind != TrackerStateKind::Idle).then_some(1),
        last_excursion: 1,
        updated_at: now() - min(1),
        hysteresis_started_at,
        awaiting_rearm: false,
        held_since: None,
    }
}

struct Case {
    rule: Rule,
    ctx: SensorContext,
    now: DateTime<Utc>,
    seed: Seed,
}

impl Case {
    fn new(rule: Rule, ctx: SensorContext) -> Self {
        Case {
            rule,
            ctx,
            now: now(),
            seed: Seed::default(),
        }
    }

    fn at(mut self, now: DateTime<Utc>) -> Self {
        self.now = now;
        self
    }

    fn seeded(mut self, seed: Seed) -> Self {
        self.seed = seed;
        self
    }

    fn evaluate(&self, state: &mut EngineState, t: DateTime<Utc>) -> Evaluation {
        quiet::evaluate(&self.rule, &self.ctx, state, t)
    }

    fn next_change(&self) -> Option<DateTime<Utc>> {
        self.evaluate(&mut self.seed.state(), self.now)
            .next_change_at
    }

    /// No node contributes: a known root has no next change, and a held one
    /// only its hold's bound, which this evaluation began.
    fn assert_contributes_nothing(&self, label: &str) {
        let e = self.evaluate(&mut self.seed.state(), self.now);
        let bound = e.root.is_unknown().then(|| self.now + HOLD_BOUND);
        assert_eq!(e.next_change_at, bound, "{label}");
    }
}

impl Case {
    fn quiet_at(&self, t: DateTime<Utc>) -> Result<(), String> {
        quiet::quiet_at(&self.rule, &self.ctx, || self.seed.state(), self.now, t)
    }
}

fn assert_quiet_until_next_change(case: &Case, label: &str) {
    if let Err(e) =
        quiet::quiet_until_next_change(&case.rule, &case.ctx, || case.seed.state(), case.now)
    {
        panic!("{label}: {e}");
    }
}

/// `N` is the expected instant, every tick before it is quiet, and the
/// evaluation at `N` or at the tick after it is not.
fn assert_next_change(case: &Case, expected: DateTime<Utc>) {
    let next = case.next_change().expect("a next change");
    assert_eq!(next, expected);
    assert_quiet_until_next_change(case, "boundary");
    if next - RESOLUTION > case.now {
        case.quiet_at(next - RESOLUTION).unwrap();
    }
    assert!(
        case.quiet_at(next).is_err() || case.quiet_at(next + RESOLUTION).is_err(),
        "nothing changed by {next}"
    );
}

const OPS: [&str; 5] = [">", ">=", "<", "<=", "=="];

/// `>` and `<=` change once the elapsed time passes the bound, a tick after
/// the others.
fn flip(op: &str, due: DateTime<Utc>) -> DateTime<Utc> {
    if matches!(op, ">" | "<=") {
        due + RESOLUTION
    } else {
        due
    }
}

#[test]
fn signal_loss_changes_when_the_timeout_elapses() {
    let case = Case::new(
        rule("signal_loss", json!({ "timeout_minutes": 15 })),
        ctx(json!({ "last_reading_at": ago(min(5)), "latest_timestamp": ago(min(5)) })),
    );
    assert_next_change(&case, now() + min(10));
}

#[test]
fn signal_loss_already_lost_or_cold_has_none() {
    let lost = Case::new(
        rule("signal_loss", json!({ "timeout_minutes": 15 })),
        ctx(json!({ "last_reading_at": ago(min(20)) })),
    );
    assert_eq!(lost.next_change(), None);
    let cold = Case::new(
        rule("signal_loss", json!({ "timeout_minutes": 15 })),
        ctx(json!({})),
    );
    cold.assert_contributes_nothing("cold");
}

#[test]
fn staleness_flips_at_the_bound_for_each_operator() {
    for op in OPS {
        let case = Case::new(
            rule("staleness", json!({ "operator": op, "value": 15 })),
            ctx(json!({ "last_reading_at": ago(min(5)) })),
        );
        assert_next_change(&case, flip(op, now() + min(10)));
    }
}

#[test]
fn equality_changes_again_just_after_the_bound() {
    let case = Case::new(
        rule("staleness", json!({ "operator": "==", "value": 15 })),
        ctx(json!({ "last_reading_at": ago(min(15)) })),
    );
    assert_next_change(&case, now() + TimeDelta::nanoseconds(100));
}

#[test]
fn every_elapsed_kind_and_operator_flips_at_its_next_change() {
    let kinds: Vec<(&str, Value, Value, TimeDelta)> = vec![
        (
            "site_age",
            json!({ "value": 24 }),
            json!({ "last_site_change_at": ago(TimeDelta::hours(20)) }),
            TimeDelta::hours(4),
        ),
        (
            "sensor_age",
            json!({ "value": 10 }),
            json!({ "last_sensor_start_at": ago(TimeDelta::days(9)) }),
            TimeDelta::days(1),
        ),
        (
            "loop_stale",
            json!({ "minutes": 20 }),
            json!({ "last_aps_cycle_at": ago(min(5)), "has_ever_aps_cycled": true }),
            min(15),
        ),
        (
            "loop_enaction_stale",
            json!({ "minutes": 20 }),
            json!({ "last_aps_enacted_at": ago(min(5)), "has_ever_aps_cycled": true }),
            min(15),
        ),
        (
            "time_since_last_carb",
            json!({ "minutes": 30 }),
            json!({ "last_carb_at": ago(min(10)) }),
            min(20),
        ),
        (
            "time_since_last_bolus",
            json!({ "minutes": 30 }),
            json!({ "last_bolus_at": ago(min(10)) }),
            min(20),
        ),
        (
            "tracker_age",
            json!({ "tracker_definition_id": TRACKER, "minutes": -30 }),
            json!({ "active_trackers": [{
                "tracker_definition_id": TRACKER,
                "reference_at": (now() + min(60)).to_rfc3339(),
                "scheduled": true
            }] }),
            min(30),
        ),
    ];
    for (kind, mut params, context, due) in kinds {
        for op in OPS {
            params["operator"] = json!(op);
            let case = Case::new(rule(kind, params.clone()), ctx(context.clone()));
            // A negative elapsed time truncates toward zero, so it flips
            // within the tick before, reported at the start of that tick.
            let expected = if kind == "tracker_age" {
                flip(op, now() + due) - RESOLUTION
            } else {
                flip(op, now() + due)
            };
            assert_next_change(&case, expected);
        }
    }
}

#[test]
fn for_minutes_kinds_change_when_the_span_has_held() {
    let started = json!({ "started_at": ago(min(4)) });
    let cases = [
        (
            "pump_suspended",
            json!({ "is_active": true, "for_minutes": 10 }),
            json!({ "active_pump_suspension": started, "has_ever_pump_snapshot": true }),
        ),
        (
            "override_active",
            json!({ "is_active": true, "for_minutes": 10 }),
            json!({ "active_override": started }),
        ),
        (
            "do_not_disturb",
            json!({ "is_active": true, "for_minutes": 10 }),
            json!({ "active_do_not_disturb": { "started_at": ago(min(4)), "source": "manual" } }),
        ),
        (
            "pump_state",
            json!({ "mode": "Automatic", "is_active": true, "for_minutes": 10 }),
            json!({ "active_pump_modes": [{ "mode": "Automatic", "started_at": ago(min(4)) }] }),
        ),
        (
            "pump_state",
            json!({ "mode": "Suspended", "is_active": true, "for_minutes": 10 }),
            json!({ "active_pump_modes": [
                { "mode": "Automatic", "started_at": ago(min(60)) },
                { "mode": "Suspended", "started_at": ago(min(4)) }
            ] }),
        ),
        (
            "pump_state",
            json!({ "mode": "Automatic", "is_active": true, "for_minutes": 10 }),
            json!({ "active_pump_modes": [
                { "mode": "Turbo", "started_at": ago(min(60)) },
                { "mode": "Automatic", "started_at": ago(min(4)) }
            ] }),
        ),
        (
            "pump_state",
            json!({ "mode": "Automatic", "is_active": true, "for_minutes": 10 }),
            json!({ "active_pump_modes": [{ "mode": "Turbo", "started_at": ago(min(4)) }] }),
        ),
        (
            "state_span_active",
            json!({ "category": "Override", "is_active": true, "for_minutes": 10 }),
            json!({ "active_state_spans": [{ "category": "Override", "state": null, "started_at": ago(min(4)) }] }),
        ),
        (
            "alert_state",
            json!({ "alert_id": OTHER, "state": "firing", "for_minutes": 10 }),
            json!({ "active_alerts": [{ "alert_id": OTHER, "state": "firing", "triggered_at": ago(min(4)) }] }),
        ),
    ];
    for (kind, params, context) in cases {
        let case = Case::new(rule(kind, params), ctx(context));
        assert_next_change(&case, now() + min(6));
    }
}

#[test]
fn a_guard_that_holds_the_leaf_constant_contributes_nothing() {
    let cases = [
        (
            "loop_stale",
            json!({ "operator": ">", "minutes": 20 }),
            json!({ "last_aps_cycle_at": ago(min(5)), "has_ever_aps_cycled": false }),
        ),
        (
            "pump_suspended",
            json!({ "is_active": true, "for_minutes": 10 }),
            json!({ "active_pump_suspension": { "started_at": ago(min(4)) } }),
        ),
        (
            "override_active",
            json!({ "is_active": true }),
            json!({ "active_override": { "started_at": ago(min(4)) } }),
        ),
        (
            "pump_state",
            json!({ "mode": "Automatic", "is_active": true }),
            json!({ "active_pump_modes": [{ "mode": "Turbo", "started_at": ago(min(4)) }] }),
        ),
        (
            "pump_state",
            json!({ "mode": "Automatic", "is_active": false, "for_minutes": 10 }),
            json!({ "active_pump_modes": [{ "mode": "Turbo", "started_at": ago(min(4)) }] }),
        ),
        (
            "time_since_last_carb",
            json!({ "operator": ">", "minutes": 30 }),
            json!({}),
        ),
        (
            "tracker_age",
            json!({ "tracker_definition_id": TRACKER, "operator": ">=", "minutes": 20 }),
            json!({ "active_trackers": [{
                "tracker_definition_id": TRACKER,
                "reference_at": ago(min(30)),
                "recorded_at": now().to_rfc3339()
            }] }),
        ),
        (
            "threshold",
            json!({ "direction": "below", "value": 70 }),
            json!({ "latest_value": 60 }),
        ),
    ];
    for (kind, params, context) in cases {
        Case::new(rule(kind, params), ctx(context)).assert_contributes_nothing(kind);
    }
}

/// A recorded duration run whose `>=` threshold had not passed when recorded
/// still fires, and its due time is the next change.
#[test]
fn tracker_age_recorded_before_due_changes_when_due() {
    let case = Case::new(
        rule(
            "tracker_age",
            json!({ "tracker_definition_id": TRACKER, "operator": ">=", "minutes": 60 }),
        ),
        ctx(json!({ "active_trackers": [{
            "tracker_definition_id": TRACKER,
            "reference_at": ago(min(30)),
            "recorded_at": ago(min(20))
        }] })),
    );
    assert_next_change(&case, now() + min(30));
}

/// The decimal conversion keeps 15 significant digits, a step of 60 ticks at
/// 100 million minutes, so an elapsed time just short of the bound rounds up
/// to it and holds before the exact instant.
#[test]
fn decimal_rounding_moves_the_flip_before_the_exact_bound() {
    let case = Case::new(
        rule(
            "tracker_age",
            json!({ "tracker_definition_id": TRACKER, "operator": ">=", "minutes": 100_000_000 }),
        ),
        ctx(json!({ "active_trackers": [{
            "tracker_definition_id": TRACKER,
            "reference_at": ago(min(99_999_999)),
        }] })),
    );
    let exact = now() + min(1);
    let next = case.next_change().unwrap();
    assert!(next < exact, "{next} is not before {exact}");
    assert_next_change(&case, next);
}

#[test]
fn sustained_over_a_reading_driven_child_changes_when_its_window_ends() {
    let case = Case::new(
        rule(
            "sustained",
            json!({ "minutes": 15, "child": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } } }),
        ),
        ctx(json!({ "latest_value": 60 })),
    );
    assert_eq!(case.next_change(), Some(now() + min(15)));
    let running = Case::new(case.rule.clone(), case.ctx.clone()).seeded(Seed {
        timers: vec![("sustained", TimerState::Running(now() - min(5)))],
        tracker: None,
    });
    assert_next_change(&running, now() + min(10));
}

/// A run starts where its child began, so its window ends that many minutes
/// on from there, not from the evaluation; the end of the path's not-true
/// lease bounds the start.
#[test]
fn sustained_over_a_child_that_began_earlier_changes_when_the_window_from_its_start_ends() {
    let suspended = |not_true: TimeDelta| {
        Case::new(
            rule(
                "sustained",
                json!({ "minutes": 30, "child": { "type": "pump_suspended", "pump_suspended": { "is_active": true } } }),
            ),
            ctx(json!({
                "has_ever_pump_snapshot": true,
                "active_pump_suspension": { "started_at": ago(min(20)) },
            })),
        )
        .seeded(Seed {
            timers: vec![("sustained", TimerState::NotTrue(now() - not_true))],
            tracker: None,
        })
    };
    assert_next_change(&suspended(min(25)), now() + min(10));
    // Recorded 10 minutes ago, its lease ends 5 minutes ago: the run starts there.
    assert_next_change(&suspended(min(10)), now() + min(25));
}

#[test]
fn sustained_over_a_false_child_has_only_its_childs() {
    let case = Case::new(
        rule(
            "sustained",
            json!({ "minutes": 15, "child": { "type": "threshold", "threshold": { "direction": "below", "value": 70 } } }),
        ),
        ctx(json!({ "latest_value": 120 })),
    );
    assert_eq!(case.next_change(), None);
}

/// The hostile review's counterexample: a flip in year 10240 is past what a
/// host can represent, so it reads as none.
#[test]
fn a_change_past_the_host_calendar_is_none() {
    let case = Case::new(
        rule("sensor_age", json!({ "operator": ">", "value": 3_000_000 })),
        ctx(json!({ "last_sensor_start_at": ago(TimeDelta::days(1)) })),
    );
    assert_eq!(case.next_change(), None);
}

#[test]
fn hysteresis_changes_when_its_window_closes() {
    let condition = json!({ "direction": "below", "value": 70 });
    let entering = Case::new(
        rule_with("threshold", condition.clone(), None, 5),
        ctx(json!({ "latest_value": 120 })),
    )
    .seeded(Seed {
        timers: vec![],
        tracker: Some(tracker(TrackerStateKind::Active, None)),
    });
    assert_next_change(&entering, now() + min(5));

    let held = Case::new(
        rule_with("threshold", condition.clone(), None, 5),
        ctx(json!({ "latest_value": 120 })),
    )
    .seeded(Seed {
        timers: vec![],
        tracker: Some(tracker(TrackerStateKind::Hysteresis, Some(now() - min(2)))),
    });
    assert_next_change(&held, now() + min(3));

    let closes_at_once = Case::new(
        rule_with("threshold", condition, None, 0),
        ctx(json!({ "latest_value": 120 })),
    )
    .seeded(Seed {
        timers: vec![],
        tracker: Some(tracker(TrackerStateKind::Active, None)),
    });
    assert_next_change(&closes_at_once, now() + RESOLUTION);
}

#[test]
fn time_of_day_changes_at_its_next_edge_and_wraps_midnight() {
    let window = json!({ "from": "22:00", "to": "06:00", "timezone": "Etc/UTC" });
    let case =
        Case::new(rule("time_of_day", window), ctx(json!({}))).at(at("2026-06-10T23:00:00Z"));
    assert_next_change(&case, at("2026-06-11T06:00:00Z"));
}

#[test]
fn time_of_day_in_the_tenant_zone() {
    let case = Case::new(
        rule("time_of_day", json!({ "from": "09:00", "to": "17:00" })),
        ctx(json!({ "tenant_time_zone_id": "Australia/Sydney" })),
    )
    .at(at("2026-06-10T12:00:00Z"));
    assert_next_change(&case, at("2026-06-10T23:00:00Z"));
}

/// Sydney skips 02:00 to 03:00 on 2026-10-04: an edge at 02:30 is the instant
/// the gap ends, 03:00 AEDT.
#[test]
fn an_edge_inside_a_gap_is_the_gap_end() {
    let case = Case::new(
        rule(
            "time_of_day",
            json!({ "from": "02:30", "to": "05:00", "timezone": "Australia/Sydney" }),
        ),
        ctx(json!({})),
    )
    .at(at("2026-10-03T15:00:00Z"));
    assert_next_change(&case, at("2026-10-03T16:00:00Z"));
}

/// Sydney repeats 02:00 to 03:00 on 2026-04-05: at 02:40 AEDT the window
/// `[01:00, 02:30)` is closed, and the fold steps the wall clock back into it.
#[test]
fn a_fold_back_over_an_edge_changes_at_the_fold() {
    let case = Case::new(
        rule(
            "time_of_day",
            json!({ "from": "01:00", "to": "02:30", "timezone": "Australia/Sydney" }),
        ),
        ctx(json!({})),
    )
    .at(at("2026-04-04T15:40:00Z"));
    assert_next_change(&case, at("2026-04-04T16:00:00Z"));
}

#[test]
fn day_of_week_changes_at_the_next_local_midnight() {
    let case = Case::new(
        rule("day_of_week", json!({ "days": ["wednesday"] })),
        ctx(json!({ "tenant_time_zone_id": "America/New_York" })),
    )
    .at(at("2026-06-10T12:00:00Z"));
    assert_next_change(&case, at("2026-06-11T04:00:00Z"));
}

#[test]
fn the_auto_resolve_tree_counts_while_auto_resolve_is_enabled() {
    let condition = json!({ "direction": "below", "value": 70 });
    let resolve = json!({ "type": "staleness", "staleness": { "operator": ">=", "value": 15 } });
    let context = ctx(json!({ "latest_value": 60, "last_reading_at": ago(min(5)) }));
    let enabled = Case::new(
        rule_with("threshold", condition.clone(), Some(resolve), 0),
        context.clone(),
    );
    assert_eq!(enabled.next_change(), Some(now() + min(10)));
    let disabled = Case::new(rule("threshold", condition), context);
    assert_eq!(disabled.next_change(), None);
}

#[test]
fn every_part_of_a_tree_counts_whatever_its_siblings_hold() {
    let tree = json!({ "operator": "and", "conditions": [
        { "type": "threshold", "threshold": { "direction": "below", "value": 70 } },
        { "type": "sustained", "sustained": { "minutes": 30, "child":
            { "type": "staleness", "staleness": { "operator": "<", "value": 60 } } } },
        { "type": "time_since_last_bolus", "time_since_last_bolus": { "operator": ">", "minutes": 20 } }
    ] });
    let case = Case::new(
        rule("composite", tree),
        ctx(
            json!({ "latest_value": 120, "last_reading_at": ago(min(1)), "last_bolus_at": ago(min(5)) }),
        ),
    );
    assert_eq!(case.next_change(), Some(now() + min(15) + RESOLUTION));
    assert_quiet_until_next_change(&case, "composite");
}

fn reading_driven_leaves() -> Vec<(&'static str, Value, Value)> {
    vec![
        (
            "threshold",
            json!({ "direction": "below", "value": 70 }),
            json!({ "latest_value": 60 }),
        ),
        (
            "rate_of_change",
            json!({ "direction": "falling", "rate": 2 }),
            json!({ "trend_rate": -3 }),
        ),
        (
            "trend",
            json!({ "bucket": "falling_fast" }),
            json!({ "trend_bucket": "falling_fast" }),
        ),
        (
            "glucose_bucket",
            json!({ "buckets": ["low"] }),
            json!({ "glucose_bucket": "low" }),
        ),
    ]
}

fn with_reading(mut fact: Value, age: TimeDelta) -> SensorContext {
    fact["latest_timestamp"] = ago(age).into();
    fact["last_reading_at"] = ago(age).into();
    ctx(fact)
}

fn fresh_for(kind: &str, params: Value, minutes: i64) -> Rule {
    let wire: WireRule = serde_json::from_value(json!({
        "id": RULE,
        "condition_type": kind,
        "condition_params": params,
        "glucose_freshness_minutes": minutes,
    }))
    .unwrap();
    Rule::try_from(wire).unwrap()
}

#[test]
fn a_reading_driven_leaf_changes_when_its_reading_goes_stale() {
    for (kind, params, fact) in reading_driven_leaves() {
        let case = Case::new(rule(kind, params), with_reading(fact, min(5)));
        assert_next_change(&case, now() + min(6));
    }
}

#[test]
fn a_rule_s_own_freshness_bound_moves_the_change() {
    for (kind, params, fact) in reading_driven_leaves() {
        let case = Case::new(fresh_for(kind, params, 30), with_reading(fact, min(5)));
        assert_next_change(&case, now() + min(25));
    }
}

#[test]
fn a_late_reading_changes_one_bound_after_it_arrived() {
    for (kind, params, fact) in reading_driven_leaves() {
        let mut late = fact;
        late["latest_received_at"] = ago(min(2)).into();
        let case = Case::new(rule(kind, params), with_reading(late, min(20)));
        assert_next_change(&case, now() + min(9));
    }
}

#[test]
fn a_reading_exactly_the_bound_old_changes_at_the_next_tick() {
    for (kind, params, fact) in reading_driven_leaves() {
        let case = Case::new(rule(kind, params), with_reading(fact, min(11)));
        assert_next_change(&case, now() + RESOLUTION);
    }
}

#[test]
fn a_stale_or_absent_glucose_fact_contributes_nothing() {
    for (kind, params, fact) in reading_driven_leaves() {
        Case::new(rule(kind, params.clone()), with_reading(fact, min(12)))
            .assert_contributes_nothing(&format!("{kind} stale"));
        Case::new(rule(kind, params), with_reading(json!({}), min(5)))
            .assert_contributes_nothing(&format!("{kind} absent"));
    }
}

/// §5.3: a determination leaf with its value turns stale where the time since the determination
/// passes the rule's freshness bound; one already stale, or without its value, contributes
/// nothing; one beyond the tolerance ahead becomes known 5 minutes before it.
#[test]
fn a_determination_leaf_changes_where_its_determination_goes_stale() {
    for kind in ["eventual_glucose", "carbs_required"] {
        let leaf = rule(kind, json!({ "operator": ">", "value": 10 }));
        let determination = |made: TimeDelta| {
            ctx(json!({ "aps_determination": {
                "at": ago(made), "eventual_mgdl": 60, "carbs_required_grams": 20
            } }))
        };
        assert_next_change(
            &Case::new(leaf.clone(), determination(min(4))),
            now() - min(4) + min(11),
        );
        Case::new(leaf.clone(), determination(min(12))).assert_contributes_nothing(kind);
        Case::new(
            leaf.clone(),
            ctx(json!({ "aps_determination": { "at": ago(min(4)) } })),
        )
        .assert_contributes_nothing(kind);
        assert_next_change(
            &Case::new(leaf, determination(-min(20))),
            now() + min(20) - min(5),
        );
    }
}
