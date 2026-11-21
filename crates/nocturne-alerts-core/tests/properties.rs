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
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerState, TrackerStateKind, Transition,
    TransitionType,
};
use nocturne_alerts_core::model::{
    ConditionKind, Node, ParseError, Payload, Reason, parse_payload,
};
use nocturne_alerts_core::sustained::{TimerOp, TimerOpKind, TimerStore};
use nocturne_alerts_core::truth::Truth;
use nocturne_alerts_core::validate::{validate_node, validate_rule};

use clock::{clock_context, naming_the_context, word_or_unrecognised};
use generators::{
    RULE_ID, Tick, arbitrary_json, base, composite, is_container, mutated, mutations, node,
    node_value, on_unknown, payload, rule_spec, simple_leaf, ticks, tree, word,
};

#[derive(Debug, Clone)]
enum Action {
    Process {
        condition_met: bool,
        auto_resolve_met: bool,
        confirmation_readings: i32,
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
        6 => (any::<bool>(), any::<bool>(), 0..4i32, -1..30i32).prop_map(
            |(condition_met, auto_resolve_met, confirmation_readings, hysteresis_minutes)| {
                Action::Process {
                    condition_met,
                    auto_resolve_met,
                    confirmation_readings,
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
            confirmation_readings,
            hysteresis_minutes,
        } => tracker.process_evaluation(
            rule,
            TrackerRuleConfig {
                confirmation_readings,
                hysteresis_minutes,
            },
            condition_met,
            auto_resolve_met,
            now,
        ),
        Action::ForceClose(reason) => tracker.force_close(rule, reason, now),
        Action::CloseElapsed { hysteresis_minutes } => tracker.close_elapsed_hysteresis(
            rule,
            TrackerRuleConfig {
                confirmation_readings: 1,
                hysteresis_minutes,
            },
            now,
        ),
    }
}

/// What the transitions seen so far say is open.
#[derive(Default)]
struct Ledger {
    open: HashMap<Uuid, u32>,
    closed: BTreeSet<u32>,
    last_opened: u32,
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
                prop_assert!(
                    ordinal > self.last_opened,
                    "ordinal {ordinal} not increasing"
                );
                prop_assert!(!self.open.contains_key(&rule), "a second excursion opened");
                self.last_opened = ordinal;
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
                prop_assert!(self.closed.insert(ordinal), "{ordinal} closed twice");
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
    fresh.set_next_excursion_ordinal(prev.next_excursion_ordinal());
    fresh
}

proptest! {
    /// An excursion opens once and closes at most once, one is open per rule
    /// at a time, ordinals only grow, the state's fields agree with its kind,
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
            prop_assert_eq!(
                continuous.next_excursion_ordinal(),
                reloaded.next_excursion_ordinal()
            );
        }
    }
}

fn rule_from_wire(wire: Value) -> Rule {
    let wire: WireRule = serde_json::from_value(wire).expect("a wire rule");
    Rule::try_from(wire).expect("a known condition type")
}

fn snapshot(state: &EngineState) -> BTreeMap<String, DateTime<Utc>> {
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
    mirror: &mut BTreeMap<String, DateTime<Utc>>,
    ops: &[TimerOp],
) -> Result<(), TestCaseError> {
    for op in ops {
        match op.kind {
            TimerOpKind::Set => {
                mirror.insert(op.path.clone(), op.at.expect("a set carries its instant"));
            }
            TimerOpKind::Clear => {
                prop_assert!(mirror.remove(&op.path).is_some(), "cleared an unset timer");
            }
            other => prop_assert!(false, "unhandled op {other:?}"),
        }
    }
    Ok(())
}

/// The leaves and Kleene connectives of the generated trees, and the
/// `sustained` rule of §3 stated as a run start per path.
#[derive(Default)]
struct Model {
    run_starts: BTreeMap<String, DateTime<Utc>>,
}

impl Model {
    fn eval(&mut self, node: &Value, path: &str, ctx: &Value, now: DateTime<Utc>) -> Truth {
        let kind = node["type"].as_str().expect("a typed node");
        let payload = &node[kind];
        let child = |this: &mut Self, value: &Value, index: usize| {
            let own = format!("{path}[{index}].{}", value["type"].as_str().unwrap());
            this.eval(value, &own, ctx, now)
        };
        match kind {
            "threshold" => {
                let value = payload["value"].as_i64().unwrap();
                match ctx["latest_value"].as_i64() {
                    None => Truth::Unknown,
                    Some(latest) if payload["direction"] == "above" => (latest > value).into(),
                    Some(latest) => (latest < value).into(),
                }
            }
            "iob" => {
                let value = payload["value"].as_i64().unwrap();
                let Some(iob) = ctx["iob_units"].as_i64() else {
                    return Truth::Unknown;
                };
                match payload["operator"].as_str().unwrap() {
                    ">" => iob > value,
                    ">=" => iob >= value,
                    "<" => iob < value,
                    "<=" => iob <= value,
                    "==" => iob == value,
                    other => panic!("the generators produce no operator {other}"),
                }
                .into()
            }
            "sleep_session_active" => {
                (ctx["sleep_session_active"].as_bool() == payload["is_active"].as_bool()).into()
            }
            "not" => match child(self, &payload["child"], 0) {
                Truth::True => Truth::False,
                Truth::False => Truth::True,
                Truth::Unknown => Truth::Unknown,
            },
            "composite" => {
                let results: Vec<Truth> = payload["conditions"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .enumerate()
                    .map(|(i, c)| child(self, c, i))
                    .collect();
                let (decisive, otherwise) = if payload["operator"] == "and" {
                    (Truth::False, Truth::True)
                } else {
                    (Truth::True, Truth::False)
                };
                if results.contains(&decisive) {
                    decisive
                } else if results.contains(&Truth::Unknown) {
                    Truth::Unknown
                } else {
                    otherwise
                }
            }
            "sustained" => match child(self, &payload["child"], 0) {
                Truth::Unknown => Truth::Unknown,
                Truth::False => {
                    self.run_starts.remove(path);
                    Truth::False
                }
                Truth::True => {
                    let minutes = payload["minutes"].as_i64().unwrap();
                    match self.run_starts.get(path) {
                        None => {
                            self.run_starts.insert(path.to_owned(), now);
                            Truth::False
                        }
                        Some(first) => ((now - *first).num_seconds() >= minutes * 60).into(),
                    }
                }
            },
            other => panic!("the generators produce no {other}"),
        }
    }
}

proptest! {
    /// The returned timer ops, replayed in order onto a map, are exactly the
    /// store's timers for the rule after each tick.
    #[test]
    fn timer_ops_replayed_give_the_stored_timers(spec in rule_spec(), ticks in ticks()) {
        let rule = rule_from_wire(spec.wire());
        let mut state = EngineState::new();
        let mut mirror: BTreeMap<String, DateTime<Utc>> = BTreeMap::new();
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
        ticks in ticks(),
    ) {
        let rule = rule_from_wire(spec.wire());
        let mut continuous = EngineState::new();
        let mut mirror: BTreeMap<String, DateTime<Utc>> = BTreeMap::new();
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
    /// lasted its minutes.
    #[test]
    fn sustained_timers_follow_the_child_run_and_hold_for_their_minutes(
        spec in rule_spec(),
        ticks in ticks(),
    ) {
        let rule = rule_from_wire(spec.wire());
        let mut state = EngineState::new();
        let mut model = Model::default();
        let body = node("composite", spec.wire()["condition_params"].clone());
        let resolve = spec.wire()["auto_resolve_params"].clone();
        for tick in &ticks {
            let awaiting = state.tracker.awaiting_rearm(RULE_ID);
            let outcome = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            let expected = model.eval(&body, "composite", &tick.context, tick.now);
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
    /// §6.4: an evaluation whose unknown root the rule holds leaves the
    /// tracker as it was, `updated_at` included, except that a hysteresis
    /// window that has elapsed closes the excursion.
    #[test]
    fn a_held_evaluation_changes_nothing_but_an_elapsed_hysteresis_window(
        spec in rule_spec(),
        ticks in ticks(),
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
            let window_elapsed = before.is_some_and(|s| {
                s.state == TrackerStateKind::Hysteresis
                    && s.hysteresis_started_at.is_some_and(|started| {
                        tick.now - started >= TimeDelta::minutes(i64::from(rule.hysteresis_minutes))
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

const UNKNOWN_FLOOR_PERCENT: usize = 15;
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
    (plain_tree(), plain_tree(), 0..4i32, 0..10i32, on_unknown()).prop_map(
        |(body, resolve, confirmation_readings, hysteresis_minutes, on_unknown)| {
            let wire = json!({
                "id": RULE_ID.to_string(),
                "condition_type": "composite",
                "condition_params": composite("or", vec![body.clone()])["composite"],
                "confirmation_readings": confirmation_readings,
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
        let mut model = Model::default();
        let mut held = false;
        for tick in &ticks {
            let awaiting = state.tracker.awaiting_rearm(RULE_ID);
            prop_assert_eq!(awaiting, held);
            let before = state.tracker.state(RULE_ID).copied();
            let outcome = evaluate_rule(&rule, &context_of(tick), tick.now, &mut state, false);
            let evaluation = outcome.evaluation.expect("the body is evaluable");
            let body_truth = model.eval(&body, "composite", &tick.context, tick.now);
            let resolve_truth = model.eval(&resolve, "auto_resolve", &tick.context, tick.now);
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

/// A tracker state a rule can be left in: each of the four, and idle
/// awaiting re-arm.
fn seeded_tracker(now: DateTime<Utc>) -> BoxedStrategy<Option<TrackerState>> {
    let kinds = select(vec![
        (TrackerStateKind::Idle, false),
        (TrackerStateKind::Idle, true),
        (TrackerStateKind::Confirming, false),
        (TrackerStateKind::Active, false),
        (TrackerStateKind::Hysteresis, false),
    ]);
    option::of((kinds, 0..3i32, 0..=3600i64).prop_map(
        move |((state, awaiting_rearm), count, ago)| {
            let since = now - TimeDelta::seconds(ago);
            TrackerState {
                state,
                confirmation_count: if state == TrackerStateKind::Confirming {
                    count + 1
                } else {
                    0
                },
                active_excursion: matches!(
                    state,
                    TrackerStateKind::Active | TrackerStateKind::Hysteresis
                )
                .then_some(1),
                updated_at: since,
                hysteresis_started_at: (state == TrackerStateKind::Hysteresis).then_some(since),
                awaiting_rearm,
            }
        },
    ))
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
            0..4i32,
            0..10i32,
            clock_context(now),
            seeded_tracker(now),
            vec(1..=3600i64, 0..=2),
            on_unknown(),
        )
            .prop_map(
                move |(
                    body,
                    resolve,
                    confirmation_readings,
                    hysteresis_minutes,
                    context,
                    tracker,
                    mut earlier,
                    on_unknown,
                )| {
                    let body = naming_the_context(body);
                    let kind = body["type"].as_str().expect("a node type").to_owned();
                    earlier.sort_unstable_by(|a, b| b.cmp(a));
                    ClockCase {
                        rule: json!({
                            "id": RULE_ID.to_string(),
                            "condition_type": kind,
                            "condition_params": body[&kind],
                            "confirmation_readings": confirmation_readings,
                            "hysteresis_minutes": hysteresis_minutes,
                            "auto_resolve_enabled": resolve.is_some(),
                            "auto_resolve_params": resolve.map(naming_the_context),
                            "on_unknown": on_unknown,
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
    pump_mode: Option<&'static str>,
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
        option::of(word_or_unrecognised::<PumpMode>("Turbo")),
        vec(
            (select(categories), option::of(select(vec!["a", "b"]))),
            0..=3,
        ),
        option::of(word_or_unrecognised::<TrendBucket>("sideways")),
        option::of(word_or_unrecognised::<GlucoseBucket>("off_the_chart")),
    )
        .prop_map(|(pump_mode, spans, trend, glucose)| NamedFacts {
            pump_mode,
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
    /// the answer (`pump_state` over an active pump unless `for_minutes` has
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
                "active_pump_state",
                facts
                    .pump_mode
                    .map(|m| json!({ "mode": m, "started_at": started })),
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
        let expected = match facts.pump_mode {
            None => Truth::from(!is_active),
            Some(active) if recognised::<PumpMode>(Some(active)).is_none() => {
                if is_active && !held {
                    Truth::False
                } else {
                    Truth::Unknown
                }
            }
            Some(active) => Truth::from((active == mode) == is_active && (!is_active || held)),
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
