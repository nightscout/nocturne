//! Property tests of the excursion tracker, the timer store and the readers
//! (engine-semantics.md §1.4, §3 `sustained`, §6, §7).

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

#[path = "common/clock.rs"]
mod clock;
#[path = "common/generators.rs"]
mod generators;
#[path = "common/quiet.rs"]
mod quiet;

use std::collections::{BTreeMap, BTreeSet, HashMap};

use chrono::{DateTime, TimeDelta, Utc};
use proptest::collection::vec;
use proptest::option;
use proptest::prelude::*;
use proptest::sample::select;
use proptest::strategy::ValueTree;
use proptest::test_runner::TestRunner;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Rule, WireRule, evaluate_rule};
use nocturne_alerts_core::enums::{
    CompositeOp, GlucoseBucket, PumpMode, StateSpanCategory, TrendBucket, WireEnum,
};
use nocturne_alerts_core::eval::{Env, eval_payload};
use nocturne_alerts_core::excursion::{
    CloseReason, ExcursionTracker, HOLD_BOUND, TrackerRuleConfig, TrackerState, TrackerStateKind,
    Transition, TransitionType,
};
use nocturne_alerts_core::facts::ContextFact;
use nocturne_alerts_core::model::{
    ConditionKind, Node, ParseError, Payload, Reason, parse_payload,
};
use nocturne_alerts_core::next_change::RESOLUTION;
use nocturne_alerts_core::sustained::{TimerOp, TimerOpKind, TimerState, TimerStore};
use nocturne_alerts_core::truth::Truth;
use nocturne_alerts_core::unknown::UnknownReason;
use nocturne_alerts_core::validate::{validate_node, validate_rule};

use clock::{clock_context, enabled_at, naming_the_context, word_or_unrecognised};
use generators::{
    RULE_ID, Tick, arbitrary_json, base, composite, glucose_freshness_minutes, is_container,
    mutated, mutations, node, node_value, on_unknown, payload, rule_spec, simple_leaf,
    skewed_ticks, ticks, tree, word,
};

#[derive(Debug, Clone)]
enum Action {
    Process {
        condition_met: bool,
        auto_resolve_met: bool,
        hysteresis_minutes: i32,
    },
    ForceClose(CloseReason),
    CloseElapsed {
        hysteresis_minutes: i32,
    },
}

#[derive(Debug, Clone)]
struct Step {
    rule: usize,
    action: Action,
    advance_minutes: i64,
}

fn action() -> impl Strategy<Value = Action> {
    let reason = select(vec![
        CloseReason::Hysteresis,
        CloseReason::AutoResolve,
        CloseReason::Manual,
        CloseReason::RuleDisabled,
    ]);
    prop_oneof![
        6 => (any::<bool>(), any::<bool>(), -1..30i32).prop_map(
            |(condition_met, auto_resolve_met, hysteresis_minutes)| {
                Action::Process {
                    condition_met,
                    auto_resolve_met,
                    hysteresis_minutes,
                }
            }
        ),
        1 => reason.prop_map(Action::ForceClose),
        2 => (-1..30i32).prop_map(|hysteresis_minutes| Action::CloseElapsed { hysteresis_minutes }),
    ]
}

fn steps() -> impl Strategy<Value = (usize, Vec<Step>)> {
    (1..=2usize).prop_flat_map(|rules| {
        let step =
            (0..rules, action(), 0..40i64).prop_map(|(rule, action, advance_minutes)| Step {
                rule,
                action,
                advance_minutes,
            });
        (Just(rules), vec(step, 0..60))
    })
}

fn rule_ids(rules: usize) -> Vec<Uuid> {
    (1..=rules)
        .map(|n| Uuid::from_u128(u128::try_from(n).expect("a small rule count")))
        .collect()
}

fn run(
    tracker: &mut ExcursionTracker,
    rule: Uuid,
    action: &Action,
    now: DateTime<Utc>,
) -> Transition {
    match *action {
        Action::Process {
            condition_met,
            auto_resolve_met,
            hysteresis_minutes,
        } => tracker.process_evaluation(
            rule,
            TrackerRuleConfig { hysteresis_minutes },
            condition_met,
            auto_resolve_met,
            now,
        ),
        Action::ForceClose(reason) => tracker.force_close(rule, reason, now),
        Action::CloseElapsed { hysteresis_minutes } => {
            tracker.close_elapsed_hysteresis(rule, TrackerRuleConfig { hysteresis_minutes }, now)
        }
    }
}

/// What the transitions seen so far say is open.
#[derive(Default)]
struct Ledger {
    open: HashMap<Uuid, u32>,
    closed: BTreeSet<(Uuid, u32)>,
    last_opened: HashMap<Uuid, u32>,
}

impl Ledger {
    fn observe(&mut self, rule: Uuid, t: Transition) -> Result<(), TestCaseError> {
        match t.kind {
            TransitionType::None => {
                prop_assert_eq!(t.excursion, None);
                prop_assert_eq!(t.close_reason, None);
            }
            TransitionType::ExcursionOpened => {
                let ordinal = t.excursion.expect("an opened excursion has an ordinal");
                let last = self.last_opened.get(&rule).copied().unwrap_or(0);
                prop_assert_eq!(ordinal, last + 1, "a rule numbers its excursions 1, 2, ...");
                prop_assert!(!self.open.contains_key(&rule), "a second excursion opened");
                self.last_opened.insert(rule, ordinal);
                self.open.insert(rule, ordinal);
            }
            TransitionType::ExcursionContinues
            | TransitionType::HysteresisStarted
            | TransitionType::HysteresisResumed => {
                prop_assert_eq!(self.open.get(&rule).copied(), t.excursion);
            }
            TransitionType::ExcursionClosed => {
                let ordinal = t.excursion.expect("a closed excursion has an ordinal");
                prop_assert_eq!(self.open.remove(&rule), Some(ordinal));
                prop_assert!(
                    self.closed.insert((rule, ordinal)),
                    "{ordinal} closed twice"
                );
                prop_assert!(t.close_reason.is_some());
            }
            other => prop_assert!(false, "unhandled transition {other:?}"),
        }
        Ok(())
    }
}

fn check_state_shape(
    tracker: &ExcursionTracker,
    ledger: &Ledger,
    rule: Uuid,
) -> Result<(), TestCaseError> {
    prop_assert_eq!(
        tracker.active_excursion_id(rule),
        ledger.open.get(&rule).copied()
    );
    let Some(state) = tracker.state(rule) else {
        return Ok(());
    };
    let open = matches!(
        state.state,
        TrackerStateKind::Active | TrackerStateKind::Hysteresis
    );
    prop_assert_eq!(state.active_excursion.is_some(), open);
    prop_assert_eq!(tracker.active_excursion_id(rule).is_some(), open);
    prop_assert_eq!(
        state.hysteresis_started_at.is_some(),
        state.state == TrackerStateKind::Hysteresis
    );
    prop_assert!(!state.awaiting_rearm || state.state == TrackerStateKind::Idle);
    Ok(())
}

/// §6.3: only an auto-resolve close from active sets the hold; while it is set
/// an evaluation finding both trees true opens nothing and keeps it, and one
/// finding either false clears it.
fn check_rearm(
    before: Option<TrackerState>,
    action: &Action,
    after: Option<TrackerState>,
    t: Transition,
) -> Result<(), TestCaseError> {
    let held_before = before.is_some_and(|s| s.awaiting_rearm);
    let held_after = after.is_some_and(|s| s.awaiting_rearm);
    let was_active = before.is_some_and(|s| s.state == TrackerStateKind::Active);
    match *action {
        Action::ForceClose(reason) if t.kind == TransitionType::ExcursionClosed => {
            prop_assert_eq!(held_after, reason == CloseReason::AutoResolve && was_active);
        }
        Action::ForceClose(_) | Action::CloseElapsed { .. } => {
            prop_assert_eq!(held_after, held_before);
        }
        Action::Process {
            condition_met,
            auto_resolve_met,
            ..
        } => {
            if held_before && condition_met && auto_resolve_met {
                prop_assert!(held_after);
                prop_assert_eq!(t.kind, TransitionType::None);
            } else {
                prop_assert!(!held_after);
            }
        }
    }
    Ok(())
}

/// §6.1: a hysteresis close happens exactly when the window has elapsed, on a
/// step that evaluates it.
fn check_hysteresis_window(
    before: Option<TrackerState>,
    action: &Action,
    now: DateTime<Utc>,
    t: Transition,
) -> Result<(), TestCaseError> {
    let window = match *action {
        Action::Process {
            condition_met: false,
            hysteresis_minutes,
            ..
        }
        | Action::CloseElapsed { hysteresis_minutes } => hysteresis_minutes,
        Action::Process { .. } => {
            let closed = t.kind == TransitionType::ExcursionClosed
                && t.close_reason == Some(CloseReason::Hysteresis);
            prop_assert!(
                !closed,
                "a hysteresis close on a tick whose condition holds"
            );
            return Ok(());
        }
        Action::ForceClose(_) => return Ok(()),
    };
    let elapsed = before.is_some_and(|s| {
        s.state == TrackerStateKind::Hysteresis
            && s.hysteresis_started_at
                .is_some_and(|started| now - started >= TimeDelta::minutes(i64::from(window)))
    });
    let closed = t.kind == TransitionType::ExcursionClosed
        && t.close_reason == Some(CloseReason::Hysteresis);
    prop_assert_eq!(closed, elapsed);
    Ok(())
}

fn rebuilt(prev: &ExcursionTracker, rules: &[Uuid]) -> ExcursionTracker {
    let mut fresh = ExcursionTracker::new();
    for rule in rules {
        if let Some(state) = prev.state(*rule) {
            fresh.restore_state(*rule, *state);
        }
    }
    fresh
}

proptest! {
    /// An excursion opens once and closes at most once, one is open per rule
    /// at a time, a rule numbers its excursions 1, 2, ..., the state's fields agree with its kind,
    /// and the re-arm hold and hysteresis window follow their rules.
    #[test]
    fn tracker_keeps_one_excursion_per_rule_and_follows_its_rules((rules, steps) in steps()) {
        let ids = rule_ids(rules);
        let mut tracker = ExcursionTracker::new();
        let mut ledger = Ledger::default();
        let mut now = base();
        for step in &steps {
            now += TimeDelta::minutes(step.advance_minutes);
            let rule = ids[step.rule];
            let before = tracker.state(rule).copied();
            let transition = run(&mut tracker, rule, &step.action, now);
            ledger.observe(rule, transition)?;
            check_rearm(before, &step.action, tracker.state(rule).copied(), transition)?;
            check_hysteresis_window(before, &step.action, now, transition)?;
            for id in &ids {
                check_state_shape(&tracker, &ledger, *id)?;
            }
        }
    }

    /// Rebuilding the tracker from its persisted state before every step
    /// changes neither the transition nor the state.
    #[test]
    fn tracker_persisted_between_steps_behaves_as_if_it_never_was((rules, steps) in steps()) {
        let ids = rule_ids(rules);
        let mut continuous = ExcursionTracker::new();
        let mut reloaded = ExcursionTracker::new();
        let mut now = base();
        for step in &steps {
            now += TimeDelta::minutes(step.advance_minutes);
            let rule = ids[step.rule];
            reloaded = rebuilt(&reloaded, &ids);
            let a = run(&mut continuous, rule, &step.action, now);
            let b = run(&mut reloaded, rule, &step.action, now);
            prop_assert_eq!(a, b);
            for id in &ids {
                prop_assert_eq!(
                    format!("{:?}", continuous.state(*id)),
                    format!("{:?}", reloaded.state(*id))
                );
            }
        }
    }
}

fn rule_from_wire(wire: Value) -> Rule {
    let wire: WireRule = serde_json::from_value(wire).expect("a wire rule");
    Rule::try_from(wire).expect("a known condition type")
}

fn snapshot(state: &EngineState) -> BTreeMap<String, TimerState> {
    state
        .timers
        .snapshot_for_rule(RULE_ID)
        .map(|(path, at)| (path.to_owned(), at))
        .collect()
}

fn context_of(tick: &Tick) -> SensorContext {
    serde_json::from_value(tick.context.clone()).unwrap()
}

fn apply_ops(
    mirror: &mut BTreeMap<String, TimerState>,
    ops: &[TimerOp],
) -> Result<(), TestCaseError> {
    for op in ops {
        let state = match op.kind {
            TimerOpKind::Set => TimerState::Running(op.at),
            TimerOpKind::Clear => TimerState::NotTrue(op.at),
            other => return Err(TestCaseError::fail(format!("unhandled op {other:?}"))),
        };
        prop_assert!(
            mirror.insert(op.path.clone(), state) != Some(state),
            "an op that changes nothing"
        );
    }
    Ok(())
}

/// §5.4, stated apart from the engine: the time from `anchor` to `now`,
/// zero for an anchor up to 5 minutes ahead (exactly 5 included), and not
/// known for one further ahead.
fn model_since(now: DateTime<Utc>, anchor: DateTime<Utc>) -> Option<TimeDelta> {
    let passed = now - anchor;
    if passed >= TimeDelta::zero() {
        Some(passed)
    } else if passed >= -TimeDelta::minutes(5) {
        Some(TimeDelta::zero())
    } else {
        None
    }
}

/// The leaves and Kleene connectives of the generated trees, the reading
/// freshness of §3, and the `sustained` rule of §3 stated as each path's run
/// start or last not-true evaluation. A node evaluates to its truth and, when
/// true, the instant §3 says it has held since.
struct Model {
    run_starts: BTreeMap<String, TimerState>,
    glucose_freshness_minutes: i64,
}

impl Model {
    /// For a rule whose own bound is `minutes`, the engine's default when
    /// absent.
    fn new(minutes: Option<i64>) -> Self {
        Model {
            run_starts: BTreeMap::new(),
            glucose_freshness_minutes: minutes.unwrap_or(11),
        }
    }

    /// More than the bound has passed since the reading arrived, else since its
    /// timestamp: exactly the bound is fresh. An arrival after `now` is fresh;
    /// a timestamp follows the clock tolerance, so beyond it the reading's
    /// freshness is not known.
    fn stale(&self, ctx: &Value, now: DateTime<Utc>) -> Truth {
        let bound = TimeDelta::minutes(self.glucose_freshness_minutes);
        let instant = |field: &str| {
            ctx[field]
                .as_str()
                .map(|at| at.parse::<DateTime<Utc>>().expect("an instant"))
        };
        if let Some(arrived) = instant("latest_received_at") {
            return (now - arrived > bound).into();
        }
        instant("latest_timestamp").map_or(Truth::False, |at| {
            model_since(now, at).map_or(Truth::Unknown, |passed| (passed > bound).into())
        })
    }

    fn eval(
        &mut self,
        node: &Value,
        path: &str,
        ctx: &Value,
        now: DateTime<Utc>,
    ) -> (Truth, Option<DateTime<Utc>>) {
        let kind = node["type"].as_str().expect("a typed node");
        let payload = &node[kind];
        let child = |this: &mut Self, value: &Value, index: usize| {
            let own = format!("{path}[{index}].{}", value["type"].as_str().unwrap());
            this.eval(value, &own, ctx, now)
        };
        let unheld = |truth: Truth| (truth, None);
        match kind {
            "threshold" => {
                let value = payload["value"].as_i64().unwrap();
                let truth = match ctx["latest_value"]
                    .as_i64()
                    .filter(|_| self.stale(ctx, now).is_false())
                {
                    None => Truth::Unknown,
                    Some(latest) if payload["direction"] == "above" => (latest > value).into(),
                    Some(latest) => (latest < value).into(),
                };
                let read_at = ctx["latest_timestamp"]
                    .as_str()
                    .map(|at| at.parse::<DateTime<Utc>>().expect("an instant"));
                (truth, read_at.filter(|_| truth.is_true()))
            }
            "iob" => {
                let value = payload["value"].as_i64().unwrap();
                let Some(iob) = ctx["iob_units"].as_i64() else {
                    return unheld(Truth::Unknown);
                };
                unheld(
                    match payload["operator"].as_str().unwrap() {
                        ">" => iob > value,
                        ">=" => iob >= value,
                        "<" => iob < value,
                        "<=" => iob <= value,
                        "==" => iob == value,
                        other => panic!("the generators produce no operator {other}"),
                    }
                    .into(),
                )
            }
            "sleep_session_active" => unheld(
                (ctx["sleep_session_active"].as_bool() == payload["is_active"].as_bool()).into(),
            ),
            "not" => unheld(match child(self, &payload["child"], 0).0 {
                Truth::True => Truth::False,
                Truth::False => Truth::True,
                Truth::Unknown => Truth::Unknown,
            }),
            "composite" => {
                let results: Vec<(Truth, Option<DateTime<Utc>>)> = payload["conditions"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .enumerate()
                    .map(|(i, c)| child(self, c, i))
                    .collect();
                let truths: Vec<Truth> = results.iter().map(|r| r.0).collect();
                let and = payload["operator"] == "and";
                let (decisive, otherwise) = if and {
                    (Truth::False, Truth::True)
                } else {
                    (Truth::True, Truth::False)
                };
                let truth = if truths.contains(&decisive) {
                    decisive
                } else if truths.contains(&Truth::Unknown) {
                    Truth::Unknown
                } else {
                    otherwise
                };
                let since = if and {
                    results
                        .iter()
                        .map(|r| r.1)
                        .collect::<Option<Vec<_>>>()
                        .and_then(|s| s.into_iter().max())
                } else {
                    results
                        .iter()
                        .filter(|r| r.0.is_true())
                        .filter_map(|r| r.1)
                        .min()
                };
                (truth, since.filter(|_| truth.is_true()))
            }
            "sustained" => {
                let (truth, since) = child(self, &payload["child"], 0);
                match truth {
                    Truth::Unknown => unheld(Truth::Unknown),
                    Truth::False => {
                        let leased = matches!(
                            self.run_starts.get(path),
                            Some(TimerState::NotTrue(last)) if now - *last < TimeDelta::minutes(5)
                        );
                        if !leased {
                            self.run_starts
                                .insert(path.to_owned(), TimerState::NotTrue(now));
                        }
                        unheld(Truth::False)
                    }
                    Truth::True => {
                        let minutes = TimeDelta::minutes(payload["minutes"].as_i64().unwrap());
                        let start = match self.run_starts.get(path) {
                            Some(TimerState::Running(first)) => *first,
                            other => {
                                let bound = match other {
                                    Some(TimerState::NotTrue(last)) => {
                                        (*last + TimeDelta::minutes(5)).min(now)
                                    }
                                    _ => now,
                                };
                                let start = since.unwrap_or(now).max(bound).min(now);
                                self.run_starts
                                    .insert(path.to_owned(), TimerState::Running(start));
                                start
                            }
                        };
                        match model_since(now, start) {
                            None => unheld(Truth::Unknown),
                            Some(passed) if passed >= minutes => {
                                (Truth::True, Some(start + minutes))
                            }
                            Some(_) => unheld(Truth::False),
                        }
                    }
                }
            }
            other => panic!("the generators produce no {other}"),
        }
    }
}

proptest! {
    /// The returned timer ops, replayed in order onto a map, are exactly the
    /// store's timers for the rule after each tick.
    #[test]
    fn timer_ops_replayed_give_the_stored_timers(spec in rule_spec(), ticks in skewed_ticks()) {
        let rule = rule_from_wire(spec.wire());
        let mut state = EngineState::new();
        let mut mirror: BTreeMap<String, TimerState> = BTreeMap::new();
        for tick in &ticks {
            let outcome = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, true);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            apply_ops(&mut mirror, &evaluation.timer_ops)?;
            prop_assert_eq!(&mirror, &snapshot(&state));
        }
    }

    /// An engine state rebuilt before every tick from the replayed timers and
    /// the persisted tracker evaluates exactly as the continuous one does.
    #[test]
    fn engine_state_rebuilt_every_tick_evaluates_as_the_continuous_one(
        spec in rule_spec(),
        ticks in skewed_ticks(),
    ) {
        let rule = rule_from_wire(spec.wire());
        let mut continuous = EngineState::new();
        let mut mirror: BTreeMap<String, TimerState> = BTreeMap::new();
        for tick in &ticks {
            let ctx = context_of(tick);

            let mut reloaded = EngineState::new();
            for (path, at) in &mirror {
                reloaded.timers.seed(RULE_ID, path, *at);
            }
            reloaded.tracker = rebuilt(&continuous.tracker, &[RULE_ID]);

            let a = evaluate_rule(&rule, &ctx, tick.now, &mut continuous, true);
            let b = evaluate_rule(&rule, &ctx, tick.now, &mut reloaded, true);
            prop_assert_eq!(a.to_json(), b.to_json());
            apply_ops(&mut mirror, &a.evaluation.expect("evaluable").timer_ops)?;
            prop_assert_eq!(&mirror, &snapshot(&reloaded));
        }
    }

    /// §3 `sustained`: after a tick a node's timer exists exactly while its
    /// child's run is unbroken and holds the first tick the child was true in
    /// it; an unknown child neither starts nor breaks a run and leaves the
    /// node unknown. The node is true once a run begun on an earlier tick has
    /// lasted its minutes, measured by the clock tolerance of §5.4 when the
    /// clock has stepped back past the run's start.
    #[test]
    fn sustained_timers_follow_the_child_run_and_hold_for_their_minutes(
        spec in rule_spec(),
        ticks in skewed_ticks(),
    ) {
        let rule = rule_from_wire(spec.wire());
        let mut state = EngineState::new();
        let mut model = Model::new(spec.glucose_freshness_minutes);
        let body = node("composite", spec.wire()["condition_params"].clone());
        let resolve = spec.wire()["auto_resolve_params"].clone();
        for tick in &ticks {
            let awaiting = state.tracker.awaiting_rearm(RULE_ID);
            let outcome = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            let (expected, _) = model.eval(&body, "composite", &tick.context, tick.now);
            prop_assert_eq!(evaluation.root, expected);
            let open = evaluation.auto_resolved
                || state.tracker.active_excursion_id(RULE_ID).is_some();
            if !resolve.is_null() && (awaiting || open) {
                model.eval(&resolve, "auto_resolve", &tick.context, tick.now);
            }
            prop_assert_eq!(snapshot(&state), model.run_starts.clone());
        }
    }
}

/// `spec`'s body wrapped in a root `sustained` of `minutes`, so the root's
/// truth is that node's.
fn sustained_root(spec: &generators::RuleSpec, minutes: i64) -> Rule {
    let mut wire = spec.wire();
    let body = node("composite", wire["condition_params"].clone());
    wire["condition_type"] = "sustained".into();
    wire["condition_params"] = json!({ "minutes": minutes, "child": body });
    rule_from_wire(wire)
}

/// What one tick of a root-`sustained` run shows: how many `sustained` starts
/// it set, how many began before `now`, how many of those its path's last
/// not-true evaluation bounded, and whether the root held.
#[derive(Debug, Default, Clone, Copy)]
struct StartTally {
    starts: usize,
    earlier: usize,
    bounded: usize,
    roots_true: usize,
}

/// §3 `sustained`, checked on one tick from the ops it returned and the state
/// before it: every start is at or before `now`, and at or after the end of
/// its path's not-true lease, five minutes past the recorded instant, or
/// `now` when that is earlier (or `now`, for a path with no state, the rule
/// having no `enabled_at`); and the root holds only once `minutes` have passed
/// since its run's start.
fn check_starts(
    before: &BTreeMap<String, TimerState>,
    ops: &[TimerOp],
    root: Truth,
    after: &BTreeMap<String, TimerState>,
    now: DateTime<Utc>,
    minutes: i64,
) -> Result<StartTally, TestCaseError> {
    let mut tally = StartTally::default();
    for op in ops.iter().filter(|op| op.kind == TimerOpKind::Set) {
        tally.starts += 1;
        prop_assert!(op.at <= now, "{} starts at {} after {now}", op.path, op.at);
        let bound = match before.get(&op.path) {
            Some(TimerState::NotTrue(last)) => (*last + TimeDelta::minutes(5)).min(now),
            _ => now,
        };
        prop_assert!(
            op.at >= bound,
            "{} starts at {} before its bound {bound}",
            op.path,
            op.at
        );
        if op.at < now {
            tally.earlier += 1;
            if op.at == bound {
                tally.bounded += 1;
            }
        }
    }
    if root.is_true() {
        tally.roots_true += 1;
        let Some(TimerState::Running(start)) = after.get("sustained") else {
            return Err(TestCaseError::fail("a true root with no run"));
        };
        prop_assert!(
            now - *start >= TimeDelta::minutes(minutes),
            "true at {now} with a run started at {start}"
        );
    }
    Ok(tally)
}

/// Runs a root-`sustained` rule over `ticks`, checking every tick.
fn run_starts(
    spec: &generators::RuleSpec,
    minutes: i64,
    ticks: &[Tick],
) -> Result<StartTally, TestCaseError> {
    let rule = sustained_root(spec, minutes);
    let mut state = EngineState::new();
    let mut total = StartTally::default();
    for tick in ticks {
        let before = snapshot(&state);
        let evaluation = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false)
            .evaluation
            .expect("the body is evaluable");
        let tally = check_starts(
            &before,
            &evaluation.timer_ops,
            evaluation.root,
            &snapshot(&state),
            tick.now,
            minutes,
        )?;
        total.starts += tally.starts;
        total.earlier += tally.earlier;
        total.bounded += tally.bounded;
        total.roots_true += tally.roots_true;
    }
    Ok(total)
}

/// A child whose truth and start the context states, and the instant it
/// began to hold: a pump suspension started then, a low reading taken and
/// received then, or a signal lost then (the last reading `timeout` before);
/// or an `and` / `or` of a suspension and an override that begin at two
/// instants, which holds from the later / the earlier of them.
#[derive(Debug, Clone, Copy)]
enum Begins {
    Suspension,
    LowReading,
    SignalLoss,
    AllOf,
    AnyOf,
}

/// A child that is false at `base()`, the first evaluation, and true with one
/// start from `began()` on (`base() + began_after`, or for a composite the
/// instant its connective gives from that and `base() + second_after`); the minutes of the `sustained` over
/// it; and the instants after `base()` it is evaluated at, as the seconds
/// since `base()`, with the subset a sparser host evaluates at. No evaluation
/// falls in the five minutes before the child begins: a not-true instant
/// recorded there would bound the start by its lease, past where the child
/// began, so the start would depend on which evaluations were made.
#[derive(Debug, Clone)]
struct CadenceCase {
    begins: Begins,
    began_after: i64,
    second_after: i64,
    minutes: i64,
    evaluations: Vec<i64>,
    sparse: Vec<bool>,
}

impl CadenceCase {
    fn first(&self) -> DateTime<Utc> {
        base() + TimeDelta::seconds(self.began_after)
    }

    fn second(&self) -> DateTime<Utc> {
        base() + TimeDelta::seconds(self.second_after)
    }

    /// When the child begins to hold: an `and` once both have begun, an `or`
    /// once either has.
    fn began(&self) -> DateTime<Utc> {
        match self.begins {
            Begins::AllOf => self.first().max(self.second()),
            Begins::AnyOf => self.first().min(self.second()),
            Begins::Suspension | Begins::LowReading | Begins::SignalLoss => self.first(),
        }
    }

    fn rule(&self) -> Rule {
        let child = match self.begins {
            Begins::Suspension => node("pump_suspended", json!({ "is_active": true })),
            Begins::LowReading => node("threshold", json!({ "direction": "below", "value": 70 })),
            Begins::SignalLoss => node(
                "signal_loss",
                json!({ "timeout_minutes": self.began_after / 60 }),
            ),
            Begins::AllOf | Begins::AnyOf => composite(
                if matches!(self.begins, Begins::AllOf) {
                    "and"
                } else {
                    "or"
                },
                vec![
                    node("pump_suspended", json!({ "is_active": true })),
                    node("override_active", json!({ "is_active": true })),
                ],
            ),
        };
        rule_from_wire(json!({
            "id": RULE_ID.to_string(),
            "condition_type": "sustained",
            "condition_params": { "minutes": self.minutes, "child": child },
            "glucose_freshness_minutes": 1440,
        }))
    }

    fn context(&self, now: DateTime<Utc>) -> SensorContext {
        let began = self.first();
        let ctx = match self.begins {
            Begins::Suspension => {
                let mut ctx = json!({ "has_ever_pump_snapshot": true });
                if now >= began {
                    ctx["active_pump_suspension"] = json!({ "started_at": began.to_rfc3339() });
                }
                ctx
            }
            Begins::LowReading => {
                let (at, value) = if now >= began {
                    (began, 60)
                } else {
                    (base(), 150)
                };
                json!({
                    "latest_value": value,
                    "latest_timestamp": at.to_rfc3339(),
                    "latest_received_at": at.to_rfc3339(),
                    "last_reading_at": at.to_rfc3339(),
                })
            }
            Begins::SignalLoss => json!({
                "latest_value": 120,
                "latest_timestamp": base().to_rfc3339(),
                "last_reading_at": base().to_rfc3339(),
            }),
            Begins::AllOf | Begins::AnyOf => {
                let mut ctx = json!({ "has_ever_pump_snapshot": true });
                if now >= began {
                    ctx["active_pump_suspension"] = json!({ "started_at": began.to_rfc3339() });
                }
                if now >= self.second() {
                    ctx["active_override"] = json!({ "started_at": self.second().to_rfc3339() });
                }
                ctx
            }
        };
        serde_json::from_value(ctx).expect("a context")
    }

    /// The root at each of `instants` after the false evaluation at `base()`,
    /// and every timer op of the run.
    fn run(&self, instants: &[DateTime<Utc>]) -> (Vec<(DateTime<Utc>, Truth)>, Vec<TimerOp>) {
        let rule = self.rule();
        let mut state = EngineState::new();
        let first = evaluate_rule(&rule, &self.context(base()), base(), &mut state, false)
            .evaluation
            .expect("evaluable");
        assert_eq!(first.root, Truth::False, "{self:?}");
        let mut ops = first.timer_ops;
        let roots = instants
            .iter()
            .map(|&at| {
                let e = evaluate_rule(&rule, &self.context(at), at, &mut state, false)
                    .evaluation
                    .expect("evaluable");
                ops.extend(e.timer_ops);
                (at, e.root)
            })
            .collect();
        (roots, ops)
    }
}

fn cadence_case() -> impl Strategy<Value = CadenceCase> {
    (
        select(vec![
            Begins::Suspension,
            Begins::LowReading,
            Begins::SignalLoss,
            Begins::AllOf,
            Begins::AnyOf,
        ]),
        (5..=60i64).prop_map(|m| m * 60),
        (5..=60i64).prop_map(|m| m * 60),
        1..=60i64,
        vec(1..=7200i64, 1..=16),
        vec(any::<bool>(), 16),
    )
        .prop_map(
            |(begins, began_after, second_after, minutes, evaluations, sparse)| {
                let mut case = CadenceCase {
                    begins,
                    began_after,
                    second_after,
                    minutes,
                    evaluations,
                    sparse,
                };
                let began = (case.began() - base()).num_seconds();
                case.evaluations.retain(|&s| s >= began || s <= began - 300);
                if case.evaluations.is_empty() {
                    case.evaluations.push(began);
                }
                case.evaluations.sort_unstable();
                case.evaluations.dedup();
                case
            },
        )
}

proptest! {
    /// §3 `sustained`: on every tick a start is never after `now` and never
    /// before its path's last not-true evaluation, and a root `sustained`
    /// holds only once its minutes have passed since its run's start, whose
    /// clock steps back now and then.
    #[test]
    fn a_sustained_start_is_bounded_and_never_holds_early(
        spec in rule_spec(),
        minutes in 1..=30i64,
        ticks in skewed_ticks(),
    ) {
        run_starts(&spec, minutes, &ticks)?;
    }

    /// §3 `sustained`: over a child that states since when it has held, how
    /// many times it is evaluated in between does not change the outcome. The
    /// run starts where the child began, whichever evaluations saw it, and an
    /// evaluation every host makes reads the same root. For a composite that is
    /// the later of its children's starts under `and` and the earlier under
    /// `or`.
    #[test]
    fn a_sustained_run_does_not_depend_on_the_evaluation_cadence(case in cadence_case()) {
        let instants: Vec<DateTime<Utc>> = case
            .evaluations
            .iter()
            .map(|&s| base() + TimeDelta::seconds(s))
            .collect();
        let sparse: Vec<DateTime<Utc>> = instants
            .iter()
            .zip(&case.sparse)
            .filter(|(_, keep)| **keep)
            .map(|(at, _)| *at)
            .chain(instants.last().copied())
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        let (dense_roots, dense_ops) = case.run(&instants);
        let (sparse_roots, sparse_ops) = case.run(&sparse);
        let dense_at: BTreeMap<_, _> = dense_roots.into_iter().collect();
        for (at, root) in &sparse_roots {
            prop_assert_eq!(dense_at.get(at), Some(root), "at {}", at);
        }
        let starts = |ops: &[TimerOp]| {
            ops.iter()
                .filter(|op| op.kind == TimerOpKind::Set)
                .map(|op| op.at)
                .collect::<Vec<_>>()
        };
        let began = case.began();
        for start in starts(&dense_ops).into_iter().chain(starts(&sparse_ops)) {
            prop_assert_eq!(start, began);
        }
    }
}

/// The start properties are not vacuous. Over 256 sampled rules and skewed
/// tick runs, 191 of 1211 starts begin before their evaluation, 60 of those at
/// the end of their path's not-true lease, and 341 roots hold; in 51 of 256
/// cadence cases a run completes on the first evaluation to see its child.
/// The floors are about half.
#[test]
fn generated_sustained_starts_reach_earlier_and_bounded_starts() {
    let mut total = StartTally::default();
    for (spec, minutes, ticks) in sample(&(rule_spec(), 1..=30i64, skewed_ticks()), 256) {
        let tally = run_starts(&spec, minutes, &ticks).expect("the property holds");
        total.starts += tally.starts;
        total.earlier += tally.earlier;
        total.bounded += tally.bounded;
        total.roots_true += tally.roots_true;
    }
    assert!(
        total.earlier * 100 >= total.starts * EARLIER_START_FLOOR_PERCENT,
        "{total:?}"
    );
    assert!(
        total.bounded * 100 >= total.earlier * BOUNDED_START_FLOOR_PERCENT,
        "{total:?}"
    );
    assert!(total.roots_true >= ROOTS_TRUE_FLOOR, "{total:?}");

    let (mut cases, mut at_once) = (0usize, 0usize);
    for case in sample(&cadence_case(), 256) {
        cases += 1;
        let instants: Vec<DateTime<Utc>> = case
            .evaluations
            .iter()
            .map(|&s| base() + TimeDelta::seconds(s))
            .collect();
        let (roots, _) = case.run(&instants);
        let completes = case.began() + TimeDelta::minutes(case.minutes);
        if roots
            .iter()
            .find(|(at, _)| *at >= case.began())
            .is_some_and(|(at, root)| *at >= completes && root.is_true())
        {
            at_once += 1;
        }
    }
    assert!(
        at_once * 100 >= cases * AT_ONCE_FLOOR_PERCENT,
        "{at_once} of {cases} complete on the first evaluation to see the child"
    );
}

/// §6.4: the root as the tracker reads it under `on_unknown`; `None` holds.
fn model_met(on_unknown: &str, root: Truth) -> Option<bool> {
    match (root, on_unknown) {
        (Truth::True, _) | (Truth::Unknown, "met") => Some(true),
        (Truth::False, _) | (Truth::Unknown, "not_met") => Some(false),
        (Truth::Unknown, "hold") => None,
        (Truth::Unknown, other) => panic!("the generators produce no on_unknown {other}"),
    }
}

proptest! {
    /// §6.4: an evaluation whose unknown root the rule holds, before its
    /// hold's bound, leaves the tracker as it was, `updated_at` included,
    /// except that the first of a hold records its start (a rule with no state
    /// then holds only that) and a hysteresis window that has elapsed closes
    /// the excursion.
    #[test]
    fn a_held_evaluation_changes_nothing_but_an_elapsed_hysteresis_window(
        spec in rule_spec(),
        ticks in skewed_ticks(),
    ) {
        let wire = spec.wire();
        let holds = wire["on_unknown"] == "hold";
        let rule = rule_from_wire(wire);
        let mut state = EngineState::new();
        for tick in &ticks {
            let before = state.tracker.state(RULE_ID).copied();
            let outcome = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            if !(evaluation.root.is_unknown() && holds) {
                continue;
            }
            // §6.4: a stored hold this engine cannot have written restarts.
            let trusted = before.and_then(|s| {
                s.held_since.filter(|&since| {
                    let bound = since + HOLD_BOUND;
                    !((s.updated_at > since && s.updated_at < bound)
                        || (s.updated_at >= bound && s.state == TrackerStateKind::Active))
                })
            });
            let held_since = trusted.unwrap_or(tick.now);
            if model_since(tick.now, held_since).is_none_or(|held| held >= HOLD_BOUND) {
                continue;
            }
            let before = Some(match before {
                Some(s) => TrackerState { held_since: Some(held_since), ..s },
                None => TrackerState {
                    state: TrackerStateKind::Idle,
                    active_excursion: None,
                    last_excursion: 0,
                    updated_at: tick.now,
                    hysteresis_started_at: None,
                    awaiting_rearm: false,
                    held_since: Some(tick.now),
                },
            });
            let window_elapsed = before.is_some_and(|s| {
                s.state == TrackerStateKind::Hysteresis
                    && s.hysteresis_started_at
                        .and_then(|started| model_since(tick.now, started))
                        .is_some_and(|passed| {
                            passed >= TimeDelta::minutes(i64::from(rule.hysteresis_minutes))
                        })
            });
            if window_elapsed {
                prop_assert_eq!(evaluation.transition.kind, TransitionType::ExcursionClosed);
                prop_assert_eq!(evaluation.transition.close_reason, Some(CloseReason::Hysteresis));
            } else {
                prop_assert_eq!(evaluation.transition.kind, TransitionType::None);
                if !evaluation.auto_resolved {
                    prop_assert_eq!(
                        format!("{:?}", state.tracker.state(RULE_ID)),
                        format!("{before:?}")
                    );
                }
            }
        }
    }
}

proptest! {
    /// §5.1: under `hold` the instant a reading goes stale needs no
    /// evaluation. A leaf going from known to unknown cannot turn a known root
    /// into the other known value, so an evaluation there either reads the
    /// root an evaluation a tick earlier reads, or holds; and when it holds,
    /// the ticks after it come out as they do without it. Both runs close an
    /// elapsed hysteresis window at that instant, as the sweep does whatever
    /// it evaluates (§6.1). Only instants where nothing else is due count:
    /// another clock change there is a reason to evaluate of its own. A held
    /// evaluation that records a false child's not-true instant there moves
    /// that path's lease (§3, §5.1), so only one that records nothing must
    /// leave the ticks after it as they are. A held evaluation there also
    /// begins the hold earlier than the next would (§6.4), so the ticks after
    /// it are compared without the hold's start and next change, which the
    /// missed run never has earlier, and only while no tick reaches the bound.
    #[test]
    fn under_hold_a_missed_evaluation_at_the_staleness_instant_changes_nothing_later(
        spec in rule_spec(),
        ticks in ticks(),
    ) {
        let mut wire = spec.wire();
        wire["on_unknown"] = "hold".into();
        let rule = rule_from_wire(wire);
        let bound = TimeDelta::minutes(spec.glucose_freshness_minutes.unwrap_or(11));
        let config = TrackerRuleConfig {
            hysteresis_minutes: rule.hysteresis_minutes,
        };
        for k in 0..ticks.len() {
            let ctx = context_of(&ticks[k]);
            let Some(read_at) = ctx.latest_received_at.or(ctx.latest_timestamp) else {
                continue;
            };
            let stale_at = read_at + bound + RESOLUTION;
            if stale_at <= ticks[k].now || ticks.get(k + 1).is_some_and(|t| t.now <= stale_at) {
                continue;
            }
            let mut timeless = rule.clone();
            timeless.glucose_freshness = TimeDelta::days(3650);
            let mut state = EngineState::new();
            for t in &ticks[..k] {
                evaluate_rule(&rule, &context_of(t), t.now, &mut state, false);
            }
            let otherwise_due = evaluate_rule(&timeless, &ctx, ticks[k].now, &mut state, false)
                .evaluation
                .expect("the body is evaluable")
                .next_change_at;
            if otherwise_due.is_some_and(|due| due <= stale_at) {
                continue;
            }
            let run = |evaluate_there: bool, at: DateTime<Utc>| {
                let mut state = EngineState::new();
                for t in &ticks[..=k] {
                    evaluate_rule(&rule, &context_of(t), t.now, &mut state, false);
                }
                let _ = state.tracker.close_elapsed_hysteresis(RULE_ID, config, at);
                let there = evaluate_there.then(|| {
                    let e = evaluate_rule(&rule, &ctx, at, &mut state, false)
                        .evaluation
                        .expect("the body is evaluable");
                    (e.root, e.timer_ops.is_empty())
                });
                let later: Vec<Value> = ticks[k + 1..]
                    .iter()
                    .map(|t| evaluate_rule(&rule, &context_of(t), t.now, &mut state, false).to_json())
                    .collect();
                (there, later)
            };
            let (stale, evaluated) = run(true, stale_at);
            let (stale_root, recorded_nothing) = stale.expect("evaluated");
            if stale_root.is_unknown() {
                if !recorded_nothing
                    || ticks.last().is_some_and(|t| t.now >= stale_at + HOLD_BOUND)
                {
                    continue;
                }
                let (_, missed) = run(false, stale_at);
                for (e, m) in evaluated.iter().zip(&missed) {
                    let since = |v: &Value| {
                        v["tracker"]["held_since"]
                            .as_str()
                            .map(|s| DateTime::parse_from_rfc3339(s).expect("an instant"))
                    };
                    if let (Some(e), Some(m)) = (since(e), since(m)) {
                        prop_assert!(e <= m, "the missed run began its hold earlier");
                    }
                }
                let without_hold = |outcomes: Vec<Value>| -> Vec<Value> {
                    outcomes
                        .into_iter()
                        .map(|mut v| {
                            if let Some(o) = v.as_object_mut() {
                                o.remove("next_change_at");
                            }
                            if let Some(t) = v["tracker"].as_object_mut() {
                                t.remove("held_since");
                            }
                            v
                        })
                        .collect()
                };
                prop_assert_eq!(without_hold(evaluated), without_hold(missed));
            } else {
                let (fresh, _) = run(true, stale_at - RESOLUTION);
                prop_assert_eq!(Some(stale_root), fresh.map(|(root, _)| root));
            }
        }
    }
}

/// Generated contexts omit glucose and IOB often enough that roots are
/// unknown, and held, in a stated share of evaluations: 1015 and 684 of 3165
/// measured over 256 sampled rules and tick runs; the floors are about half.
#[test]
fn generated_evaluations_reach_unknown_and_hold_it() {
    let cases = sample(&(rule_spec(), ticks()), 256);
    let (mut evaluations, mut unknown, mut held) = (0usize, 0usize, 0usize);
    for (spec, ticks) in cases {
        let wire = spec.wire();
        let holds = wire["on_unknown"] == "hold";
        let rule = rule_from_wire(wire);
        let mut state = EngineState::new();
        for tick in &ticks {
            let evaluation = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false)
                .evaluation
                .expect("the body is evaluable");
            evaluations += 1;
            if evaluation.root.is_unknown() {
                unknown += 1;
                if holds {
                    held += 1;
                }
            }
        }
    }
    assert!(
        unknown * 100 >= evaluations * UNKNOWN_FLOOR_PERCENT,
        "{unknown} of {evaluations} unknown"
    );
    assert!(
        held * 100 >= evaluations * HELD_FLOOR_PERCENT,
        "{held} of {evaluations} held"
    );
}

/// §5.4: generated runs read an anchor or state instant beyond the clock
/// tolerance in a stated share of evaluations: 99 of 3248 over 256 sampled
/// rules and skewed tick runs, and 47 of 512 clock cases; the floors are about
/// half.
#[test]
fn generated_evaluations_read_anchors_beyond_the_clock_tolerance() {
    let mut evaluations = 0usize;
    let mut flagged = 0usize;
    for (spec, ticks) in sample(&(rule_spec(), skewed_ticks()), 256) {
        let rule = rule_from_wire(spec.wire());
        let mut state = EngineState::new();
        for tick in &ticks {
            let evaluation = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false)
                .evaluation
                .expect("the body is evaluable");
            evaluations += 1;
            if !evaluation.clock_anomaly.is_empty() {
                flagged += 1;
            }
        }
    }
    assert!(
        flagged * 100 >= evaluations * SKEWED_TICKS_FLOOR_PERCENT,
        "{flagged} of {evaluations} read an anchor beyond the tolerance"
    );

    let cases = sample(&clock_case(), 512);
    let mut evaluated = 0usize;
    let mut beyond = 0usize;
    for case in cases {
        let rule = rule_from_wire(case.rule.clone());
        let context: SensorContext =
            serde_json::from_value(case.context.clone()).expect("a context");
        let mut state = EngineState::new();
        if let Some(tracker) = case.tracker {
            state.tracker.restore_state(RULE_ID, tracker);
        }
        let Some(evaluation) =
            evaluate_rule(&rule, &context, case.now, &mut state, false).evaluation
        else {
            continue;
        };
        evaluated += 1;
        if !evaluation.clock_anomaly.is_empty() {
            beyond += 1;
        }
    }
    assert!(
        beyond * 100 >= evaluated * CLOCK_CASE_FLOOR_PERCENT,
        "{beyond} of {evaluated} clock cases read an anchor beyond the tolerance"
    );
}

const SKEWED_TICKS_FLOOR_PERCENT: usize = 1;
const CLOCK_CASE_FLOOR_PERCENT: usize = 4;

const UNKNOWN_FLOOR_PERCENT: usize = 15;
const EARLIER_START_FLOOR_PERCENT: usize = 8;
const BOUNDED_START_FLOOR_PERCENT: usize = 15;
const ROOTS_TRUE_FLOOR: usize = 170;
const AT_ONCE_FLOOR_PERCENT: usize = 10;
const HELD_FLOOR_PERCENT: usize = 10;

fn plain_tree() -> BoxedStrategy<Value> {
    simple_leaf()
        .prop_recursive(3, 10, 3, |inner| {
            prop_oneof![
                (word::<CompositeOp>(), vec(inner.clone(), 1..=3))
                    .prop_map(|(op, cs)| composite(op, cs)),
                inner.prop_map(|c| node("not", json!({ "child": c }))),
            ]
        })
        .boxed()
}

fn auto_resolving_rule() -> impl Strategy<Value = (Value, Value, Value)> {
    (plain_tree(), plain_tree(), 0..10i32, on_unknown()).prop_map(
        |(body, resolve, hysteresis_minutes, on_unknown)| {
            let wire = json!({
                "id": RULE_ID.to_string(),
                "condition_type": "composite",
                "condition_params": composite("or", vec![body.clone()])["composite"],
                "hysteresis_minutes": hysteresis_minutes,
                "auto_resolve_enabled": true,
                "auto_resolve_params": resolve,
                "on_unknown": on_unknown,
            });
            (wire, composite("or", vec![body]), resolve)
        },
    )
}

proptest! {
    /// §6.3: after an auto-resolve closes an excursion whose condition held,
    /// no excursion opens until a tick whose body or auto-resolve tree is
    /// known false, the body as `on_unknown` reads it (§6.4); a held tick
    /// keeps the hold.
    #[test]
    fn an_auto_resolved_rule_opens_nothing_until_a_tree_is_known_false(
        (wire, body, resolve) in auto_resolving_rule(),
        ticks in ticks(),
    ) {
        let on_unknown = wire["on_unknown"].as_str().expect("an on_unknown word").to_owned();
        let rule = rule_from_wire(wire);
        let mut state = EngineState::new();
        let mut model = Model::new(None);
        let mut held = false;
        for tick in &ticks {
            let awaiting = state.tracker.awaiting_rearm(RULE_ID);
            prop_assert_eq!(awaiting, held);
            let before = state.tracker.state(RULE_ID).copied();
            let outcome = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            let (body_truth, _) = model.eval(&body, "composite", &tick.context, tick.now);
            let (resolve_truth, _) = model.eval(&resolve, "auto_resolve", &tick.context, tick.now);
            prop_assert_eq!(evaluation.root, body_truth);
            let met = model_met(&on_unknown, body_truth);
            let keeps = match met {
                None => true,
                Some(met) => met && !resolve_truth.is_false(),
            };
            let was_active = before.is_some_and(|s| s.state == TrackerStateKind::Active);
            let was_open = before.is_some_and(|s| {
                matches!(s.state, TrackerStateKind::Active | TrackerStateKind::Hysteresis)
            });
            let open_after_tracker = match evaluation.transition.kind {
                TransitionType::ExcursionOpened
                | TransitionType::ExcursionContinues
                | TransitionType::HysteresisStarted
                | TransitionType::HysteresisResumed => true,
                TransitionType::ExcursionClosed => false,
                _ => was_open,
            };
            let active_after_tracker = match met {
                Some(met) => met && open_after_tracker,
                None => was_active,
            };
            let must_resolve = !awaiting && resolve_truth.is_true() && open_after_tracker;
            prop_assert_eq!(evaluation.auto_resolved, must_resolve);
            if must_resolve {
                prop_assert_eq!(state.tracker.active_excursion_id(RULE_ID), None);
            }
            if awaiting && keeps {
                prop_assert_ne!(evaluation.transition.kind, TransitionType::ExcursionOpened);
            }
            held = (evaluation.auto_resolved && active_after_tracker) || (awaiting && keeps);
        }
    }
}

/// A valid condition node rooted at `kind`.
fn node_of(kind: ConditionKind) -> BoxedStrategy<Value> {
    payload(kind, &tree())
        .prop_map(move |p| node(kind.name(), p))
        .boxed()
}

/// A valid node of any kind, a third of them rooted at a container so that
/// breakage lands below the root.
fn valid_roots() -> BoxedStrategy<Value> {
    let containers: Vec<ConditionKind> = ConditionKind::ALL
        .iter()
        .copied()
        .filter(|k| is_container(*k))
        .collect();
    prop_oneof![
        2 => select(containers).prop_flat_map(node_of),
        1 => select(ConditionKind::ALL.to_vec()).prop_flat_map(node_of),
    ]
    .boxed()
}

fn near_valid_nodes() -> BoxedStrategy<Value> {
    (valid_roots(), mutations(1..4))
        .prop_map(|(doc, ops)| mutated(doc, &ops))
        .boxed()
}

fn kinds_in(node: &Node, into: &mut BTreeSet<&'static str>) {
    into.insert(node.kind().name());
    if let Some(container) = node.container() {
        for child in container.children() {
            kinds_in(child, into);
        }
    }
}

fn sample<S: Strategy>(strategy: &S, count: usize) -> Vec<S::Value> {
    let mut runner = TestRunner::deterministic();
    (0..count)
        .map(|_| strategy.new_tree(&mut runner).unwrap().current())
        .collect()
}

#[test]
fn generators_cover_every_condition_kind() {
    let mut seen = BTreeSet::new();
    for doc in sample(&tree(), 600) {
        let node = Node::parse(&doc).expect("a generated tree is valid");
        kinds_in(&node, &mut seen);
    }
    let all: BTreeSet<&str> = ConditionKind::ALL.iter().map(|k| k.name()).collect();
    assert_eq!(seen, all);

    for kind in ConditionKind::ALL {
        for doc in sample(&node_of(*kind), 24) {
            let node = Node::parse(&doc).expect("a generated node is valid");
            assert_eq!(node.kind(), *kind);
        }
    }
}

/// 99 of 256 samples measured; the floor is a third.
#[test]
fn near_valid_nodes_are_rejected_below_the_root_in_a_third_of_cases() {
    const ROOT: &str = "auto_resolve";
    let nested = sample(&near_valid_nodes(), 256)
        .iter()
        .filter(|doc| {
            validate_node(doc, ROOT)
                .iter()
                .any(|issue| issue.path.len() > ROOT.len())
        })
        .count();
    assert!(nested >= 85, "{nested} of 256 were rejected below the root");
}

/// The node as the document it was read from, which the model has no writer
/// for: payload operands via `Serialize`, children placed by hand.
fn assemble(node: &Node) -> Value {
    let mut payload = serde_json::to_value(node.payload()).expect("a payload serialises");
    if let Some(container) = node.container() {
        let mut children: Vec<Value> = container.children().map(assemble).collect();
        let fields = payload.as_object_mut().expect("a payload is an object");
        match node.payload() {
            Payload::Composite(_) => fields.insert("conditions".into(), children.into()),
            _ => fields.insert("child".into(), children.remove(0)),
        };
    }
    node_value(node.kind().name(), payload)
}

fn body_of(payload: &Payload) -> Value {
    let wire = payload.kind().name();
    let mut node = assemble(&Node::from(payload.clone()));
    node.get_mut(wire)
        .map(Value::take)
        .expect("a node holds its payload")
}

fn without_nulls(v: &Value) -> Value {
    match v {
        Value::Object(fields) => fields
            .iter()
            .filter(|(_, value)| !value.is_null())
            .map(|(key, value)| (key.clone(), without_nulls(value)))
            .collect::<Map<_, _>>()
            .into(),
        Value::Array(items) => items.iter().map(without_nulls).collect::<Vec<_>>().into(),
        other => other.clone(),
    }
}

fn after_segment_start(text: &str) -> Option<&str> {
    let after = text.strip_prefix('[')?;
    let digits = after.bytes().take_while(u8::is_ascii_digit).count();
    if digits == 0 {
        return None;
    }
    after.get(digits..)?.strip_prefix("].")
}

/// `root` then `[index].type` segments; a type is whatever text the document
/// wrote, up to the next segment.
fn is_condition_path(path: &str, root: &str) -> bool {
    let Some(mut rest) = path.strip_prefix(root) else {
        return false;
    };
    while !rest.is_empty() {
        let Some(after) = after_segment_start(rest) else {
            return false;
        };
        rest = after
            .match_indices('[')
            .find(|(i, _)| after_segment_start(&after[*i..]).is_some())
            .map_or("", |(i, _)| &after[i..]);
    }
    true
}

fn check_issue_paths(issues: &[ParseError], root: &str) -> Result<(), TestCaseError> {
    for issue in issues {
        prop_assert!(
            is_condition_path(&issue.path, root),
            "path {:?} is not under {root:?}",
            issue.path
        );
    }
    Ok(())
}

/// Every reader and the validate entry, on one document.
fn check_readers(doc: &Value) -> Result<(), TestCaseError> {
    const ROOT: &str = "auto_resolve";

    let issues = validate_node(doc, ROOT);
    check_issue_paths(&issues, ROOT)?;
    match Node::parse_rooted(doc, ROOT) {
        Ok(node) => {
            let written = assemble(&node);
            let again = Node::parse_rooted(&written, ROOT);
            prop_assert!(again.is_ok(), "re-assembled node did not parse: {written}");
            prop_assert_eq!(assemble(&again.unwrap()), written);
        }
        Err(unevaluable) => prop_assert!(!unevaluable.0.is_empty()),
    }
    if let Err(unevaluable) = Node::parse(doc) {
        prop_assert!(!unevaluable.0.is_empty());
    }

    for kind in ConditionKind::ALL {
        check_issue_paths(&validate_rule(kind.name(), doc), kind.name())?;
        if let Ok(payload) = parse_payload(*kind, doc) {
            let written = body_of(&payload);
            let again = parse_payload(*kind, &written);
            prop_assert!(
                again.is_ok(),
                "re-assembled payload did not parse: {written}"
            );
            prop_assert_eq!(body_of(&again.unwrap()), written);
        }
    }
    Ok(())
}

#[derive(Debug, Clone, Copy)]
enum Bound {
    Positive,
    NonNegative,
    Unbounded,
}

/// The duration operands of §1.4.
const MINUTE_OPERANDS: [(ConditionKind, &str, Bound); 9] = [
    (ConditionKind::Sustained, "minutes", Bound::Positive),
    (
        ConditionKind::SignalLoss,
        "timeout_minutes",
        Bound::Positive,
    ),
    (ConditionKind::Predicted, "within_minutes", Bound::Positive),
    (ConditionKind::Staleness, "value", Bound::NonNegative),
    (ConditionKind::LoopStale, "minutes", Bound::NonNegative),
    (
        ConditionKind::LoopEnactionStale,
        "minutes",
        Bound::NonNegative,
    ),
    (
        ConditionKind::TimeSinceLastCarb,
        "minutes",
        Bound::NonNegative,
    ),
    (
        ConditionKind::TimeSinceLastBolus,
        "minutes",
        Bound::NonNegative,
    ),
    (ConditionKind::TrackerAge, "minutes", Bound::Unbounded),
];

fn operand_values() -> BoxedStrategy<i64> {
    prop_oneof![
        4 => -3..=3i64,
        1 => Just(i64::from(i32::MAX)),
        1 => Just(i64::from(i32::MAX) + 1),
        1 => Just(i64::from(i32::MIN)),
    ]
    .boxed()
}

proptest! {
    /// A generated valid tree reads, validates clean, and writes back as the
    /// document it was read from, up to null fields.
    #[test]
    fn valid_trees_read_back_as_written(doc in tree()) {
        let node = Node::parse(&doc);
        prop_assert!(node.is_ok(), "{doc}");
        prop_assert!(validate_node(&doc, "auto_resolve").is_empty());
        prop_assert_eq!(without_nulls(&assemble(&node.unwrap())), without_nulls(&doc));
        check_readers(&doc)?;
    }

    /// No reader panics on arbitrary JSON, and whatever reads, round-trips.
    #[test]
    fn readers_survive_arbitrary_json(doc in arbitrary_json()) {
        check_readers(&doc)?;
    }

    /// The same on a valid node broken in a few small ways.
    #[test]
    fn readers_survive_near_valid_nodes(doc in near_valid_nodes()) {
        check_readers(&doc)?;
    }

    /// The same on a valid payload of any kind broken in a few small ways.
    #[test]
    fn readers_survive_near_valid_payloads(
        body in select(ConditionKind::ALL.to_vec())
            .prop_flat_map(|kind| payload(kind, &tree())),
        ops in mutations(1..4),
    ) {
        check_readers(&mutated(body, &ops))?;
    }

    /// §1.4: the duration operands reject exactly the values the table names,
    /// with the reason it names, and accept the rest.
    #[test]
    fn minute_operands_are_accepted_or_rejected_as_the_spec_says(
        (kind, field, bound, body, value) in select(MINUTE_OPERANDS.to_vec())
            .prop_flat_map(|(kind, field, bound)| {
                (payload(kind, &tree()), operand_values()).prop_map(move |(mut body, value)| {
                    body[field] = Value::from(value);
                    (kind, field, bound, body, value)
                })
            }),
    ) {
        let in_range = i32::try_from(value).is_ok();
        let verdict = match bound {
            Bound::Positive if in_range && value <= 0 => Err(Reason::MinutesNotPositive(field)),
            Bound::NonNegative if in_range && value < 0 => Err(Reason::MinutesNegative(field)),
            _ if !in_range => Err(Reason::InvalidField(field)),
            _ => Ok(()),
        };
        let issues = parse_payload(kind, &body).err().map(|e| e.0);
        match verdict {
            Ok(()) => prop_assert_eq!(issues, None),
            Err(reason) => prop_assert_eq!(issues, Some(vec![ParseError::new(kind.name(), reason)])),
        }
    }
}

/// What `signal_loss` and `staleness` read: a reading's time and timestamp
/// and the rule's enablement, each present or not, as seconds before `now`.
#[derive(Debug, Clone)]
struct DataStoppedCase {
    last_reading: Option<i64>,
    latest: Option<i64>,
    enabled: Option<i64>,
    signal_loss: bool,
    operator: &'static str,
    minutes: i64,
    negated: bool,
}

fn data_stopped_case() -> impl Strategy<Value = DataStoppedCase> {
    let ago = || option::of(-3600..=7200i64);
    (
        (ago(), ago(), ago()),
        any::<bool>(),
        word::<nocturne_alerts_core::enums::CmpOp>(),
        1..=60i64,
        any::<bool>(),
    )
        .prop_map(
            |((last_reading, latest, enabled), signal_loss, operator, minutes, negated)| {
                DataStoppedCase {
                    last_reading,
                    latest,
                    enabled,
                    signal_loss,
                    operator,
                    minutes,
                    negated,
                }
            },
        )
}

/// §3 and §5: data stopped at the reading; a timestamp without a reading time
/// is forever ago; with neither, at the rule's enablement; with none of them,
/// unknown. `signal_loss` holds once its minutes have passed, `staleness`
/// compares the minutes passed with its operator. An instant up to 5 minutes
/// ahead has passed no time, and one further ahead is unknown (§5.4).
fn model_data_stopped(case: &DataStoppedCase) -> Truth {
    let passed = match (case.last_reading, case.latest, case.enabled) {
        (Some(ago), _, _) | (None, None, Some(ago)) => Some(ago),
        (None, Some(_), _) => None,
        (None, None, None) => return Truth::Unknown,
    };
    let (op, value) = if case.signal_loss {
        (">=", case.minutes * 60)
    } else {
        (case.operator, case.minutes * 60)
    };
    let Some(passed) = passed else {
        return matches!(op, ">" | ">=").into();
    };
    let passed = match passed {
        ..-300 => return Truth::Unknown,
        -300..0 => 0,
        _ => passed,
    };
    match op {
        ">" => passed > value,
        ">=" => passed >= value,
        "<" => passed < value,
        "<=" => passed <= value,
        "==" => passed == value,
        other => panic!("the generators produce no operator {other}"),
    }
    .into()
}

proptest! {
    /// §3 and §5: `signal_loss` and `staleness` with every combination of a
    /// reading, a timestamp and the rule's enablement, alone and under `not`.
    #[test]
    fn data_stopped_leaves_measure_from_the_reading_else_the_enablement(
        case in data_stopped_case(),
    ) {
        let now = base();
        let at = |ago: Option<i64>| ago.map(|s| (now - TimeDelta::seconds(s)).to_rfc3339());
        let leaf = if case.signal_loss {
            node("signal_loss", json!({ "timeout_minutes": case.minutes }))
        } else {
            node("staleness", json!({ "operator": case.operator, "value": case.minutes }))
        };
        let body = if case.negated { node("not", json!({ "child": leaf })) } else { leaf };
        let kind = body["type"].as_str().unwrap().to_owned();
        let rule = rule_from_wire(json!({
            "id": RULE_ID.to_string(),
            "condition_type": kind,
            "condition_params": body[&kind],
            "enabled_at": at(case.enabled),
        }));
        let context: SensorContext = serde_json::from_value(json!({
            "last_reading_at": at(case.last_reading),
            "latest_timestamp": at(case.latest),
        }))
        .unwrap();
        let root = evaluate_rule(&rule, &context, now, &mut EngineState::new(), false)
            .evaluation
            .expect("an evaluable rule")
            .root;
        let leaf = model_data_stopped(&case);
        prop_assert_eq!(root, if case.negated { !leaf } else { leaf });
    }
}

/// A `tracker_age` instance at an age around `now` in whole seconds, ahead of
/// it when negative: a scheduled (event) time or a duration start.
#[derive(Debug, Clone)]
struct TrackerAgeCase {
    ago: i64,
    scheduled: bool,
    operator: &'static str,
    minutes: i64,
}

fn tracker_age_case() -> impl Strategy<Value = TrackerAgeCase> {
    (
        prop_oneof![-7200..=7200i64, -300..=0i64, Just(-300i64), Just(-301i64)],
        any::<bool>(),
        word::<nocturne_alerts_core::enums::CmpOp>(),
        -120..=120i64,
    )
        .prop_map(|(ago, scheduled, operator, minutes)| TrackerAgeCase {
            ago,
            scheduled,
            operator,
            minutes,
        })
}

/// §3 and §5.4: a scheduled time is meant to be ahead, so its elapsed time
/// is negative until it; a duration start follows the clock tolerance.
fn model_tracker_age(case: &TrackerAgeCase) -> Truth {
    let passed = if case.scheduled {
        case.ago
    } else {
        match case.ago {
            ..-300 => return Truth::Unknown,
            -300..0 => 0,
            ago => ago,
        }
    };
    let value = case.minutes * 60;
    match case.operator {
        ">" => passed > value,
        ">=" => passed >= value,
        "<" => passed < value,
        "<=" => passed <= value,
        "==" => passed == value,
        other => panic!("the generators produce no operator {other}"),
    }
    .into()
}

proptest! {
    /// §3 and §5.4: `tracker_age` exempts a scheduled instance from the
    /// clock tolerance and holds a duration instance to it.
    #[test]
    fn tracker_age_measures_a_scheduled_time_ahead_and_holds_a_duration_start_to_the_tolerance(
        case in tracker_age_case(),
    ) {
        let now = base();
        let tracker = Uuid::from_u128(7);
        let rule = rule_from_wire(json!({
            "id": RULE_ID.to_string(),
            "condition_type": "tracker_age",
            "condition_params": {
                "tracker_definition_id": tracker.to_string(),
                "operator": case.operator,
                "minutes": case.minutes,
            },
        }));
        let context: SensorContext = serde_json::from_value(json!({
            "active_trackers": [{
                "tracker_definition_id": tracker.to_string(),
                "reference_at": (now - TimeDelta::seconds(case.ago)).to_rfc3339(),
                "scheduled": case.scheduled,
            }],
        }))
        .unwrap();
        let evaluation = evaluate_rule(&rule, &context, now, &mut EngineState::new(), false)
            .evaluation
            .expect("an evaluable rule");
        prop_assert_eq!(evaluation.root, model_tracker_age(&case));
        prop_assert_eq!(
            evaluation.clock_anomaly.is_empty(),
            case.scheduled || case.ago >= -300
        );
    }
}

/// A tracker state a rule can be left in: each of the three, and idle
/// awaiting re-arm, last changed up to an hour before `now` or, by a clock
/// that has since stepped back, up to 15 minutes after it.
fn seeded_tracker(now: DateTime<Utc>) -> BoxedStrategy<Option<TrackerState>> {
    let kinds = select(vec![
        (TrackerStateKind::Idle, false),
        (TrackerStateKind::Idle, true),
        (TrackerStateKind::Active, false),
        (TrackerStateKind::Hysteresis, false),
    ]);
    let ago = prop_oneof![3 => 0..=3600i64, 1 => -900..=-1i64];
    option::of(
        (kinds, ago).prop_map(move |((state, awaiting_rearm), ago)| {
            let since = now - TimeDelta::seconds(ago);
            TrackerState {
                state,
                active_excursion: matches!(
                    state,
                    TrackerStateKind::Active | TrackerStateKind::Hysteresis
                )
                .then_some(1),
                last_excursion: 1,
                updated_at: since,
                hysteresis_started_at: (state == TrackerStateKind::Hysteresis).then_some(since),
                awaiting_rearm,
                held_since: None,
            }
        }),
    )
    .boxed()
}

/// A rule of any kinds, a context holding every clock anchor, the tracker it
/// starts from, the earlier evaluations that leave its timers, and `now`.
#[derive(Debug, Clone)]
struct ClockCase {
    rule: Value,
    context: Value,
    tracker: Option<TrackerState>,
    earlier: Vec<i64>,
    now: DateTime<Utc>,
}

fn clock_case() -> impl Strategy<Value = ClockCase> {
    (0..=604_800i64, 0..10_000_000i64).prop_flat_map(|(seconds, ticks)| {
        let now = base() + TimeDelta::seconds(seconds) + TimeDelta::nanoseconds(ticks * 100);
        (
            tree(),
            option::of(tree()),
            0..10i32,
            clock_context(now),
            seeded_tracker(now),
            vec(prop_oneof![3 => 1..=3600i64, 1 => -900..=-1i64], 0..=2),
            on_unknown(),
            glucose_freshness_minutes(),
            enabled_at(now),
        )
            .prop_map(
                move |(
                    body,
                    resolve,
                    hysteresis_minutes,
                    context,
                    tracker,
                    mut earlier,
                    on_unknown,
                    glucose_freshness_minutes,
                    enabled_at,
                )| {
                    let body = naming_the_context(body);
                    let kind = body["type"].as_str().expect("a node type").to_owned();
                    earlier.sort_unstable_by(|a, b| b.cmp(a));
                    ClockCase {
                        rule: json!({
                            "id": RULE_ID.to_string(),
                            "condition_type": kind,
                            "condition_params": body[&kind],
                            "hysteresis_minutes": hysteresis_minutes,
                            "auto_resolve_enabled": resolve.is_some(),
                            "auto_resolve_params": resolve.map(naming_the_context),
                            "on_unknown": on_unknown,
                            "glucose_freshness_minutes": glucose_freshness_minutes,
                            "enabled_at": enabled_at,
                        }),
                        context,
                        tracker,
                        earlier,
                        now,
                    }
                },
            )
    })
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(512))]

    /// §5.3, from the post-state: with `N` the `next_change_at` of the
    /// evaluation at `now` (or `now + 3 days` when it has none), `N > now`,
    /// and at every sampled tick `t` in `(now, N)` an evaluation from the state
    /// the evaluation at `now` left repeats its root and leaves, with no
    /// transition but `continues` and no auto-resolve. The state before `now`
    /// is a seeded tracker and the timers left by earlier evaluations.
    #[test]
    fn the_outcome_is_constant_until_the_next_change(case in clock_case()) {
        let rule = rule_from_wire(case.rule.clone());
        let context: SensorContext = serde_json::from_value(case.context.clone()).expect("a context");
        prop_assume!(evaluate_rule(&rule, &context, case.now, &mut EngineState::new(), false)
            .evaluation
            .is_some());
        let before = || {
            let mut state = EngineState::new();
            if let Some(tracker) = case.tracker {
                state.tracker.restore_state(RULE_ID, tracker);
            }
            for &ago in &case.earlier {
                let at = case.now - TimeDelta::seconds(ago);
                evaluate_rule(&rule, &context, at, &mut state, false);
            }
            state
        };
        quiet::quiet_until_next_change(&rule, &context, before, case.now)
            .map_err(TestCaseError::fail)?;
    }
}

/// The mode, category and bucket names a context carries, each a member's name
/// or one the crate has no member for.
#[derive(Debug, Clone)]
struct NamedFacts {
    pump_modes: Vec<&'static str>,
    spans: Vec<(&'static str, Option<&'static str>)>,
    trend: Option<&'static str>,
    glucose: Option<&'static str>,
}

const PUMP_SPAN_MINUTES: i32 = 10;

fn named_facts() -> impl Strategy<Value = NamedFacts> {
    let categories: Vec<&'static str> = StateSpanCategory::ALL
        .iter()
        .filter(|c| **c != StateSpanCategory::PumpMode)
        .map(|c| c.name())
        .chain(["NotACategory"])
        .collect();
    (
        vec(word_or_unrecognised::<PumpMode>("Turbo"), 0..=3),
        vec(
            (select(categories), option::of(select(vec!["a", "b"]))),
            0..=3,
        ),
        option::of(word_or_unrecognised::<TrendBucket>("sideways")),
        option::of(word_or_unrecognised::<GlucoseBucket>("off_the_chart")),
    )
        .prop_map(|(pump_modes, spans, trend, glucose)| NamedFacts {
            pump_modes,
            spans,
            trend,
            glucose,
        })
}

fn recognised<E: WireEnum>(name: Option<&str>) -> Option<E> {
    name.and_then(E::from_name)
}

proptest! {
    /// §3, §4: a name the crate has no member for is unknown where it decides
    /// the answer (`pump_state` over an active mode, with none named, unless `for_minutes` has
    /// not yet passed, `trend` and `glucose_bucket` over a bucket), and is
    /// ignored where it could only
    /// name a category no leaf can name (`state_span_active`).
    #[test]
    fn an_unrecognised_name_is_unknown_or_ignored_as_the_spec_says(
        facts in named_facts(),
        mode in word::<PumpMode>(),
        is_active in any::<bool>(),
        for_minutes in option::of(select(vec![5i32, 10, 30])),
        category in select(
            StateSpanCategory::ALL
                .iter()
                .filter(|c| **c != StateSpanCategory::PumpMode)
                .map(|c| c.name())
                .collect::<Vec<_>>(),
        ),
        state in option::of(select(vec!["a", "b"])),
        trend in word::<TrendBucket>(),
        buckets in vec(word::<GlucoseBucket>(), 1..4),
    ) {
        let started = (base() - TimeDelta::minutes(PUMP_SPAN_MINUTES.into())).to_rfc3339();
        let context: SensorContext = serde_json::from_value(generators::object(vec![
            (
                "active_pump_modes",
                Some(
                    facts
                        .pump_modes
                        .iter()
                        .map(|m| json!({ "mode": m, "started_at": started }))
                        .collect(),
                ),
            ),
            (
                "active_state_spans",
                Some(
                    facts
                        .spans
                        .iter()
                        .map(|(c, s)| json!({ "category": c, "state": s, "started_at": started }))
                        .collect(),
                ),
            ),
            ("trend_bucket", facts.trend.map(Value::from)),
            ("glucose_bucket", facts.glucose.map(Value::from)),
        ]))
        .expect("a context");
        let at = |kind: ConditionKind, payload: Value| {
            let parsed = parse_payload(kind, &payload).expect("a payload");
            let mut timers = TimerStore::new();
            eval_payload(&parsed, kind.name(), &mut Env::new(base(), Uuid::nil(), &context, &mut timers))
        };

        let pump = at(
            ConditionKind::PumpState,
            json!({ "mode": mode, "is_active": is_active, "for_minutes": for_minutes }),
        );
        let held = for_minutes.is_none_or(|m| m <= PUMP_SPAN_MINUTES);
        let names_the_mode = facts.pump_modes.contains(&mode);
        let unrecognised = facts
            .pump_modes
            .iter()
            .any(|m| recognised::<PumpMode>(Some(*m)).is_none());
        let expected = if names_the_mode {
            Truth::from(is_active && held)
        } else if unrecognised {
            if is_active && !held {
                Truth::False
            } else {
                Truth::Unknown
            }
        } else {
            Truth::from(!is_active)
        };
        prop_assert_eq!(pump, expected);

        let span = at(
            ConditionKind::StateSpanActive,
            json!({ "category": category, "state": state, "is_active": is_active }),
        );
        let present = facts.spans.contains(&(category, state));
        prop_assert_eq!(span, Truth::from(present == is_active));

        let trend_truth = at(ConditionKind::Trend, json!({ "bucket": trend }));
        let expected = match facts.trend {
            Some(seen) if recognised::<TrendBucket>(Some(seen)).is_some() => {
                Truth::from(seen == trend)
            }
            _ => Truth::Unknown,
        };
        prop_assert_eq!(trend_truth, expected);

        let bucket_truth = at(ConditionKind::GlucoseBucket, json!({ "buckets": buckets }));
        let expected = match facts.glucose {
            Some(seen) if recognised::<GlucoseBucket>(Some(seen)).is_some() => {
                Truth::from(buckets.contains(&seen))
            }
            _ => Truth::Unknown,
        };
        prop_assert_eq!(bucket_truth, expected);
    }
}

/// The kinds of the leaves of a node's JSON in leaf-id order (engine-semantics.md §2.2).
fn leaf_kinds(node: &Value) -> Vec<String> {
    let kind = node["type"].as_str().unwrap();
    let payload = &node[kind];
    match kind {
        "composite" => payload["conditions"]
            .as_array()
            .unwrap()
            .iter()
            .flat_map(leaf_kinds)
            .collect(),
        "not" | "sustained" => leaf_kinds(&payload["child"]),
        _ => vec![kind.to_owned()],
    }
}

fn with_sensor_state(tick: &Tick, state: &str, since: Option<i64>) -> SensorContext {
    let mut context = tick.context.clone();
    let mut sensor = json!({ "state": state });
    if let Some(ago) = since {
        sensor["since"] = (tick.now - TimeDelta::seconds(ago)).to_rfc3339().into();
    }
    context["sensor_state"] = sensor;
    serde_json::from_value(context).unwrap()
}

proptest! {
    /// §3 sensor state: with no `sensor_state` leaf, a context whose sensor is ok, from any start,
    /// evaluates byte for byte as one that says nothing of the sensor, tick after tick.
    #[test]
    fn an_ok_sensor_evaluates_as_no_sensor_state(
        spec in rule_spec(),
        ticks in skewed_ticks(),
        since in option::of(-600..=7200i64),
    ) {
        let rule = rule_from_wire(spec.wire());
        let (mut absent, mut ok) = (EngineState::new(), EngineState::new());
        for tick in &ticks {
            let a = evaluate_rule(&rule, &context_of(tick), tick.now, &mut absent, true);
            let b = evaluate_rule(&rule, &with_sensor_state(tick, "ok", since), tick.now, &mut ok, true);
            prop_assert_eq!(a.to_json(), b.to_json());
        }
    }

    /// §3 sensor state: while the sensor is in any state but ok, or one the engine has no name
    /// for, no `threshold` leaf is known, and no root is unknown by the glucose fact: the
    /// sensor's state names why.
    #[test]
    fn a_sensor_that_is_not_ok_leaves_no_glucose_leaf_known(
        spec in rule_spec(),
        ticks in skewed_ticks(),
        state in select(vec!["warming_up", "calibration_needed", "error", "expired", "no_value", "rebooting"]),
    ) {
        let rule = rule_from_wire(spec.wire());
        let kinds = leaf_kinds(&node("composite", spec.wire()["condition_params"].clone()));
        let reason = if state == "rebooting" {
            UnknownReason::Unrecognised
        } else {
            UnknownReason::SensorNotOk
        };
        let mut engine = EngineState::new();
        for tick in &ticks {
            let ctx = with_sensor_state(tick, state, None);
            let outcome = evaluate_rule(&rule, &ctx, tick.now, &mut engine, true);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            let leaves = evaluation.leaves.expect("the leaves were requested");
            for (kind, truth) in kinds.iter().zip(&leaves) {
                if kind == "threshold" {
                    prop_assert!(truth.is_unknown(), "{kind} read {truth:?} under {state}");
                }
            }
            for (fact, why) in evaluation.unknown.iter() {
                prop_assert_ne!(fact, ContextFact::Glucose);
                if fact == ContextFact::SensorState {
                    prop_assert_eq!(why, reason);
                }
            }
        }
    }
}

fn forecast() -> BoxedStrategy<Value> {
    vec(
        (0..=60i32, 40..=300i32)
            .prop_map(|(offset, mgdl)| json!({ "offset_minutes": offset, "mgdl": mgdl })),
        0..=4,
    )
    .prop_map(Value::from)
    .boxed()
}

fn curves_and_determination() -> BoxedStrategy<(Value, Value)> {
    (
        vec(option::of(forecast()), 6),
        option::of((
            -600..=1800i64,
            option::of(40..=300i32),
            option::of(0..=60i32),
        )),
    )
        .prop_map(|(series, determination)| {
            let names = ["iob", "cob", "uam", "zt", "default", "ar2"];
            let curves: Map<String, Value> = names
                .iter()
                .zip(series)
                .filter_map(|(name, s)| Some(((*name).to_owned(), s?)))
                .collect();
            let determination = determination.map_or(Value::Null, |(ago, eventual, carbs)| {
                json!({
                    "at": (base() - TimeDelta::seconds(ago)).to_rfc3339(),
                    "eventual_mgdl": eventual,
                    "carbs_required_grams": carbs,
                })
            });
            (Value::Object(curves), determination)
        })
        .boxed()
}

proptest! {
    /// §3 `predicted`, §4: a rule that names no curve and reads no determination evaluates byte
    /// for byte the same whether or not the context carries named curves and a determination, tick
    /// after tick: only `default` (`predictions`, the old wire shape) reaches it.
    #[test]
    fn named_curves_and_a_determination_move_no_rule_that_does_not_name_them(
        spec in rule_spec(),
        predicted in (select(vec!["<", "<=", ">", ">=", "=="]), 40..=300i32, 1..=60i32),
        ticks in skewed_ticks(),
        forecasts in vec((forecast(), curves_and_determination()), 24),
    ) {
        let body = spec.wire()["condition_params"].clone();
        let (op, value, within) = predicted;
        let mut wire = spec.wire();
        wire["condition_params"] = json!({ "operator": "or", "conditions": [
            node("composite", body),
            node("predicted", json!({ "operator": op, "value": value, "within_minutes": within })),
        ] });
        let rule = rule_from_wire(wire);
        let (mut without, mut with) = (EngineState::new(), EngineState::new());
        for (tick, (default, (curves, determination))) in ticks.iter().zip(&forecasts) {
            let mut old = tick.context.clone();
            old["predictions"] = default.clone();
            let mut new = old.clone();
            new["prediction_curves"] = curves.clone();
            new["aps_determination"] = determination.clone();
            let old: SensorContext = serde_json::from_value(old).unwrap();
            let new: SensorContext = serde_json::from_value(new).unwrap();
            let a = evaluate_rule(&rule, &old, tick.now, &mut without, true);
            let b = evaluate_rule(&rule, &new, tick.now, &mut with, true);
            prop_assert_eq!(a.to_json(), b.to_json());
        }
    }
}
