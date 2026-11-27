//! An unknown root names the facts that decided it (engine-semantics.md
//! §2.4), and a hold is recorded and bounded (§6.4), checked against models
//! stated apart from the engine.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    clippy::as_conversions,
    reason = "test code"
)]

use std::collections::BTreeSet;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use proptest::prelude::*;
use proptest::strategy::ValueTree;
use proptest::test_runner::TestRunner;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::clock_anomaly::ClockAnchor;
use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Rule, WireRule, evaluate_rule};
use nocturne_alerts_core::enums::WireEnum;
use nocturne_alerts_core::excursion::{
    CloseReason, HOLD_BOUND, TrackerState, TrackerStateKind, TransitionType,
};
use nocturne_alerts_core::facts::ContextFact;
use nocturne_alerts_core::model::{CURRENT_SCHEMA, SCHEMA_VERSION};
use nocturne_alerts_core::truth::Truth;
use nocturne_alerts_core::unknown::UnknownReason;

const RULE_ID: Uuid = Uuid::from_u128(1);

fn base() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 12, 0, 0).unwrap()
}

/// A rule whose body `params` names schema 2 (engine-semantics.md §1.2).
fn rule(condition_type: &str, mut params: Value) -> Rule {
    params[SCHEMA_VERSION] = json!(CURRENT_SCHEMA);
    let wire: WireRule = serde_json::from_value(json!({
        "id": RULE_ID,
        "condition_type": condition_type,
        "condition_params": params,
    }))
    .unwrap();
    Rule::try_from(wire).unwrap()
}

fn sample<S: Strategy>(strategy: &S, count: usize) -> Vec<S::Value> {
    let mut runner = TestRunner::deterministic();
    (0..count)
        .map(|_| strategy.new_tree(&mut runner).unwrap().current())
        .collect()
}

// ---------------------------------------------------------------------------
// The named facts against an oracle from two-valued logic
// ---------------------------------------------------------------------------

/// A leaf whose fact the context sets to true, false or absent, each on its
/// own fact, so a leaf's truth and the fact it is unknown by are known
/// without the engine.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Leaf {
    Iob,
    Cob,
    PumpBattery,
    UploaderBattery,
    Transmitter,
    Sensitivity,
}

const LEAVES: [Leaf; 6] = [
    Leaf::Iob,
    Leaf::Cob,
    Leaf::PumpBattery,
    Leaf::UploaderBattery,
    Leaf::Transmitter,
    Leaf::Sensitivity,
];

impl Leaf {
    fn fact(self) -> ContextFact {
        match self {
            Leaf::Iob => ContextFact::Insulin,
            Leaf::Cob => ContextFact::Carbs,
            Leaf::PumpBattery => ContextFact::Pump,
            Leaf::UploaderBattery => ContextFact::Uploader,
            Leaf::Transmitter => ContextFact::Transmitter,
            Leaf::Sensitivity => ContextFact::Aps,
        }
    }

    fn node(self) -> Value {
        match self {
            Leaf::Iob => json!({"type": "iob", "iob": {"operator": ">", "value": 1}}),
            Leaf::Cob => json!({"type": "cob", "cob": {"operator": ">", "value": 5}}),
            Leaf::PumpBattery => {
                json!({"type": "pump_battery", "pump_battery": {"operator": "<", "value": 20}})
            }
            Leaf::UploaderBattery => json!({"type": "uploader_battery",
                "uploader_battery": {"operator": "<", "value": 20}}),
            Leaf::Transmitter => json!({"type": "transmitter_battery",
                "transmitter_battery": {"metric": "voltage_a", "operator": "<", "value": 300}}),
            Leaf::Sensitivity => json!({"type": "sensitivity_ratio",
                "sensitivity_ratio": {"operator": ">", "value": 1}}),
        }
    }

    /// The context fields that make this leaf `truth`.
    fn set(self, ctx: &mut serde_json::Map<String, Value>, truth: Truth) {
        let (field, yes, no): (&str, i64, i64) = match self {
            Leaf::Iob => ("iob_units", 2, 0),
            Leaf::Cob => ("cob_grams", 10, 0),
            Leaf::PumpBattery => ("pump_battery_percent", 10, 50),
            Leaf::UploaderBattery => ("uploader_battery_percent", 10, 50),
            Leaf::Transmitter => ("transmitter_voltage_a", 200, 400),
            Leaf::Sensitivity => ("sensitivity_ratio", 2, 0),
        };
        let guard = match self {
            Leaf::PumpBattery => Some("has_ever_pump_snapshot"),
            Leaf::UploaderBattery => Some("has_ever_uploader_snapshot"),
            Leaf::Sensitivity => Some("has_ever_aps_sensitivity"),
            _ => None,
        };
        if let Some(guard) = guard {
            ctx.insert(guard.into(), true.into());
        }
        match truth {
            Truth::True => ctx.insert(field.into(), yes.into()),
            Truth::False => ctx.insert(field.into(), no.into()),
            Truth::Unknown => None,
        };
    }
}

#[derive(Debug, Clone)]
enum Tree {
    Leaf(Leaf),
    Not(Box<Tree>),
    All(Vec<Tree>),
    Any(Vec<Tree>),
}

fn tree() -> impl Strategy<Value = Tree> {
    let leaf = proptest::sample::select(LEAVES.to_vec()).prop_map(Tree::Leaf);
    let subtree = leaf.prop_recursive(3, 16, 3, |inner| {
        prop_oneof![
            inner.clone().prop_map(|t| Tree::Not(Box::new(t))),
            proptest::collection::vec(inner.clone(), 1..=3).prop_map(Tree::All),
            proptest::collection::vec(inner, 1..=3).prop_map(Tree::Any),
        ]
    });
    prop_oneof![
        1 => subtree.clone(),
        2 => proptest::collection::vec(subtree.clone(), 2..=4).prop_map(Tree::All),
        2 => proptest::collection::vec(subtree, 2..=4).prop_map(Tree::Any),
    ]
}

impl Tree {
    fn node(&self) -> Value {
        match self {
            Tree::Leaf(l) => l.node(),
            Tree::Not(t) => json!({"type": "not", "not": {"child": t.node()}}),
            Tree::All(ts) => composite("and", ts),
            Tree::Any(ts) => composite("or", ts),
        }
    }

    /// Every leaf occurrence in pre-order.
    fn leaves(&self, out: &mut Vec<Leaf>) {
        match self {
            Tree::Leaf(l) => out.push(*l),
            Tree::Not(t) => t.leaves(out),
            Tree::All(ts) | Tree::Any(ts) => ts.iter().for_each(|t| t.leaves(out)),
        }
    }

    /// Two-valued evaluation, each leaf occurrence read from `values` in
    /// pre-order.
    fn eval(&self, values: &mut impl Iterator<Item = bool>) -> bool {
        match self {
            Tree::Leaf(_) => values.next().unwrap(),
            Tree::Not(t) => !t.eval(values),
            Tree::All(ts) => ts.iter().map(|t| t.eval(values)).fold(true, |a, b| a & b),
            Tree::Any(ts) => ts.iter().map(|t| t.eval(values)).fold(false, |a, b| a | b),
        }
    }
}

fn composite(op: &str, ts: &[Tree]) -> Value {
    json!({"type": "composite", "composite": {
        "operator": op,
        "conditions": ts.iter().map(Tree::node).collect::<Vec<_>>(),
    }})
}

/// (fact, reason) by wire name.
type Named = BTreeSet<(&'static str, &'static str)>;

fn named(fact: ContextFact, reason: UnknownReason) -> (&'static str, &'static str) {
    (fact.name(), reason.name())
}

/// What the oracle says of one case: the root over every two-valued
/// completion of the unknown leaves, and the leaves that decide it.
struct Oracle {
    root: Truth,
    named: Named,
    /// Unknown leaves that decide nothing although the root is unknown.
    undeciding: usize,
}

/// A leaf occurrence decides the root when, for some completion of the other
/// unknown leaves, the root differs between it true and it false. The root
/// is unknown when completions disagree.
fn oracle(tree: &Tree, truths: &[Truth]) -> Oracle {
    let mut leaves = Vec::new();
    tree.leaves(&mut leaves);
    let unknown: Vec<usize> = (0..leaves.len())
        .filter(|&i| truths[leaves[i] as usize].is_unknown())
        .collect();
    let run = |assign: u64, flip: Option<(usize, bool)>| -> bool {
        let values: Vec<bool> = (0..leaves.len())
            .map(|i| {
                if let Some((at, v)) = flip
                    && at == i
                {
                    return v;
                }
                match truths[leaves[i] as usize] {
                    Truth::True => true,
                    Truth::False => false,
                    Truth::Unknown => {
                        let bit = unknown.iter().position(|&u| u == i).unwrap();
                        assign >> bit & 1 == 1
                    }
                }
            })
            .collect();
        tree.eval(&mut values.into_iter())
    };
    let completions = 1u64 << unknown.len();
    let results: BTreeSet<bool> = (0..completions).map(|a| run(a, None)).collect();
    let root = match results.len() {
        2 => Truth::Unknown,
        _ => Truth::from(*results.iter().next().unwrap()),
    };
    let mut found = Named::new();
    let mut undeciding = 0;
    for &i in &unknown {
        let decides = (0..completions).any(|a| run(a, Some((i, true))) != run(a, Some((i, false))));
        if decides {
            found.insert(named(leaves[i].fact(), UnknownReason::Absent));
        } else {
            undeciding += 1;
        }
    }
    Oracle {
        root,
        named: found,
        undeciding,
    }
}

fn truths() -> impl Strategy<Value = Vec<Truth>> {
    proptest::collection::vec(
        prop_oneof![Just(Truth::True), Just(Truth::False), Just(Truth::Unknown)],
        LEAVES.len(),
    )
}

fn engine_named(tree: &Tree, truths: &[Truth]) -> (Truth, Named) {
    let node = tree.node();
    let kind = node["type"].as_str().unwrap().to_owned();
    let rule = rule(&kind, node[&kind].clone());
    let mut ctx = serde_json::Map::new();
    for (leaf, &truth) in LEAVES.iter().zip(truths) {
        leaf.set(&mut ctx, truth);
    }
    let ctx: SensorContext = serde_json::from_value(Value::Object(ctx)).unwrap();
    let e = evaluate_rule(&rule, &ctx, base(), &mut EngineState::new(), false)
        .evaluation
        .unwrap();
    (e.root, e.unknown.iter().map(|(f, r)| named(f, r)).collect())
}

proptest! {
    /// The root and the named facts are exactly the oracle's: a fact is named
    /// only through a leaf that decides the root, and none when it is known.
    #[test]
    fn the_named_facts_are_exactly_those_whose_unknown_decides_the_root(
        tree in tree(),
        truths in truths(),
    ) {
        let expected = oracle(&tree, &truths);
        let (root, named) = engine_named(&tree, &truths);
        prop_assert_eq!(root, expected.root);
        prop_assert_eq!(named, expected.named);
    }
}

/// The oracle property is not vacuous. Over 600 sampled cases, 158 roots are
/// unknown, 42 of them with an unknown leaf that decides nothing, and 78 name
/// more than one fact. The floors are about half.
#[test]
fn the_named_facts_property_reaches_undeciding_leaves_and_several_facts() {
    let (mut unknown, mut with_undeciding, mut several) = (0, 0, 0);
    for (tree, truths) in sample(&(tree(), truths()), 600) {
        let o = oracle(&tree, &truths);
        if o.root.is_unknown() {
            unknown += 1;
            with_undeciding += usize::from(o.undeciding > 0);
            several += usize::from(o.named.len() > 1);
        }
    }
    eprintln!(
        "{unknown} unknown roots, {with_undeciding} with an undeciding leaf, {several} naming several facts"
    );
    assert!(unknown >= 79, "{unknown}");
    assert!(with_undeciding >= 21, "{with_undeciding}");
    assert!(several >= 39, "{several}");
}

// ---------------------------------------------------------------------------
// Held-since and the bound against a model of §6.4
// ---------------------------------------------------------------------------

/// One evaluation: the reading at that instant is low (true), normal
/// (false) or missing (unknown).
#[derive(Debug, Clone, Copy)]
struct Step {
    after: TimeDelta,
    root: Truth,
}

fn steps() -> impl Strategy<Value = Vec<Step>> {
    let gap = prop_oneof![
        3 => (1i64..=30).prop_map(TimeDelta::minutes),
        2 => (1i64..=20).prop_map(TimeDelta::hours),
        1 => (23 * 60 + 55..=24 * 60 + 5i64).prop_map(TimeDelta::minutes),
    ];
    let root = prop_oneof![
        1 => Just(Truth::True),
        1 => Just(Truth::False),
        3 => Just(Truth::Unknown),
    ];
    proptest::collection::vec(
        (gap, root).prop_map(|(after, root)| Step { after, root }),
        1..40,
    )
}

fn context_for(root: Truth, at: DateTime<Utc>) -> SensorContext {
    let value = match root {
        Truth::True => json!(60),
        Truth::False => json!(100),
        Truth::Unknown => return SensorContext::default(),
    };
    serde_json::from_value(json!({
        "latest_value": value,
        "latest_timestamp": at,
        "last_reading_at": at,
    }))
    .unwrap()
}

proptest! {
    /// Held-since is set on exactly the first held evaluation of a hold and
    /// cleared by the first known root; the bound is reported at most once
    /// per hold, at the first evaluation at or past it; and an excursion is
    /// closed by the bound only after 24 hours of unbroken hold.
    #[test]
    fn a_hold_starts_ends_and_reaches_its_bound_as_the_model_says(steps in steps()) {
        let rule = rule("threshold", json!({"operator": "<", "value": 70}));
        let mut state = EngineState::new();
        let mut now = base();
        let mut hold: Option<DateTime<Utc>> = None;
        let mut reported = false;
        for step in &steps {
            now += step.after;
            let e = evaluate_rule(&rule, &context_for(step.root, now), now, &mut state, false)
                .evaluation
                .unwrap();
            prop_assert_eq!(e.root, step.root);
            if step.root.is_unknown() {
                let first = hold.is_none();
                let since = *hold.get_or_insert(now);
                if first {
                    reported = false;
                }
                let due = now - since >= HOLD_BOUND;
                prop_assert_eq!(e.held_too_long, due && !reported, "reported at {}", now);
                reported |= due;
            } else {
                hold = None;
                prop_assert!(!e.held_too_long);
            }
            prop_assert_eq!(e.tracker.and_then(|t| t.held_since), hold);
            if e.transition.close_reason == Some(CloseReason::HoldBound) {
                let since = hold.expect("closed by the bound while held");
                prop_assert!(now - since >= HOLD_BOUND, "closed early at {}", now);
            }
            if e.transition.kind == TransitionType::HysteresisStarted && step.root.is_unknown() {
                let since = hold.expect("a hysteresis begun while held");
                prop_assert!(now - since >= HOLD_BOUND, "hysteresis begun early at {}", now);
            }
        }
    }
}

/// The hold property is not vacuous: over 256 sampled runs, 151 report a
/// bound, 58 report two or more, and 51 close an excursion by it. The floors
/// are about half.
#[test]
fn the_hold_property_reaches_reports_and_closes() {
    let rule = rule("threshold", json!({"operator": "<", "value": 70}));
    let (mut reporting, mut twice, mut closing) = (0, 0, 0);
    for steps in sample(&steps(), 256) {
        let mut state = EngineState::new();
        let mut now = base();
        let (mut reports, mut closes) = (0, 0);
        for step in &steps {
            now += step.after;
            let e = evaluate_rule(&rule, &context_for(step.root, now), now, &mut state, false)
                .evaluation
                .unwrap();
            reports += usize::from(e.held_too_long);
            closes += usize::from(e.transition.close_reason == Some(CloseReason::HoldBound));
        }
        reporting += usize::from(reports > 0);
        twice += usize::from(reports > 1);
        closing += usize::from(closes > 0);
    }
    eprintln!("{reporting} runs report, {twice} report twice, {closing} close by the bound");
    assert!(reporting >= 75, "{reporting}");
    assert!(twice >= 29, "{twice}");
    assert!(closing >= 25, "{closing}");
}

// ---------------------------------------------------------------------------
// A stored hold this engine cannot have written (engine-semantics.md §6.4)
// ---------------------------------------------------------------------------

fn low_rule() -> Rule {
    rule("threshold", json!({"operator": "<", "value": 70}))
}

fn restored(state: TrackerState) -> EngineState {
    let mut engine = EngineState::new();
    engine.tracker.restore_state(RULE_ID, state);
    engine
}

/// The reviewer's case: a hold recorded at T0, then an engine that does not
/// record holds opened an alert at T0 + 2 days, then this engine sees the data
/// stop at T0 + 3 days. The alert must not close minutes later.
#[test]
fn an_alert_opened_after_a_stale_hold_waits_its_own_24_hours() {
    let t0 = base();
    let rule = low_rule();
    let mut state = restored(TrackerState {
        state: TrackerStateKind::Active,
        active_excursion: Some(1),
        last_excursion: 1,
        updated_at: t0 + TimeDelta::days(2),
        hysteresis_started_at: None,
        awaiting_rearm: false,
        held_since: Some(t0),
    });
    let stops = t0 + TimeDelta::days(3);
    let held = |state: &mut EngineState, at| {
        evaluate_rule(&rule, &SensorContext::default(), at, state, false)
            .evaluation
            .unwrap()
    };

    let first = held(&mut state, stops);
    assert_eq!(first.transition.kind, TransitionType::None);
    assert!(!first.held_too_long);
    assert_eq!(first.tracker.and_then(|t| t.held_since), Some(stops));

    let later = held(&mut state, stops + TimeDelta::minutes(15));
    assert_eq!(later.transition.kind, TransitionType::None);

    let bound = held(&mut state, stops + HOLD_BOUND);
    assert!(bound.held_too_long);
    assert_eq!(bound.transition.kind, TransitionType::HysteresisStarted);
}

/// An older engine's write after the hold began, before its bound: the hold
/// restarts, so it is not reported at the stale bound.
#[test]
fn a_hold_written_over_before_its_bound_restarts() {
    let t0 = base();
    let mut state = restored(TrackerState {
        state: TrackerStateKind::Idle,
        active_excursion: None,
        last_excursion: 0,
        updated_at: t0 + TimeDelta::hours(2),
        hysteresis_started_at: None,
        awaiting_rearm: false,
        held_since: Some(t0),
    });
    let at = t0 + TimeDelta::hours(30);
    let e = evaluate_rule(
        &low_rule(),
        &SensorContext::default(),
        at,
        &mut state,
        false,
    )
    .evaluation
    .unwrap();
    assert!(!e.held_too_long);
    assert_eq!(e.tracker.and_then(|t| t.held_since), Some(at));
}

/// The host's clock went back 30 minutes after a decision, then the data
/// stopped. The stored decision is ahead of every held evaluation until
/// `t0 + 30m`, and must not restart the hold at each one.
#[test]
fn a_decision_ahead_of_the_clock_starts_the_hold_once_and_is_reported() {
    let t0 = base();
    let rule = low_rule();
    let mut state = restored(TrackerState {
        state: TrackerStateKind::Active,
        active_excursion: Some(1),
        last_excursion: 1,
        updated_at: t0 + TimeDelta::minutes(30),
        hysteresis_started_at: None,
        awaiting_rearm: false,
        held_since: None,
    });
    let held = |state: &mut EngineState, at| {
        evaluate_rule(&rule, &SensorContext::default(), at, state, false)
            .evaluation
            .unwrap()
    };
    let updated_ahead = |e: &nocturne_alerts_core::engine::Evaluation| {
        e.clock_anomaly
            .iter()
            .find(|&(anchor, _)| anchor == ClockAnchor::UpdatedAt)
            .map(|(_, by)| by)
    };

    let first = held(&mut state, t0);
    assert_eq!(updated_ahead(&first), Some(TimeDelta::minutes(30)));
    let tracker = first.tracker.unwrap();
    assert_eq!(tracker.held_since, Some(t0));
    assert_eq!(tracker.updated_at, t0);

    for at in [t0 + TimeDelta::minutes(5), t0 + TimeDelta::minutes(40)] {
        let e = held(&mut state, at);
        assert_eq!(updated_ahead(&e), None);
        assert!(!e.held_too_long);
        assert_eq!(e.tracker.and_then(|t| t.held_since), Some(t0));
    }

    let bound = held(&mut state, t0 + HOLD_BOUND);
    assert!(bound.held_too_long);
    assert_eq!(bound.transition.kind, TransitionType::HysteresisStarted);

    let closed = held(&mut state, t0 + HOLD_BOUND + TimeDelta::minutes(1));
    assert_eq!(closed.transition.kind, TransitionType::ExcursionClosed);
    assert_eq!(closed.transition.close_reason, Some(CloseReason::HoldBound));
}

/// Within the tolerance a decision ahead of the clock is not reported, and the
/// hold it starts still counts once.
#[test]
fn a_decision_ahead_within_the_tolerance_starts_the_hold_once_unreported() {
    let t0 = base();
    let rule = low_rule();
    let mut state = restored(TrackerState {
        state: TrackerStateKind::Active,
        active_excursion: Some(1),
        last_excursion: 1,
        updated_at: t0 + TimeDelta::minutes(3),
        hysteresis_started_at: None,
        awaiting_rearm: false,
        held_since: None,
    });
    for at in [t0, t0 + TimeDelta::minutes(1)] {
        let e = evaluate_rule(&rule, &SensorContext::default(), at, &mut state, false)
            .evaluation
            .unwrap();
        assert!(e.clock_anomaly.is_empty(), "{at}");
        assert_eq!(e.tracker.and_then(|t| t.held_since), Some(t0), "{at}");
    }
}

/// A stored state an older engine wrote after a hold began: `updated_at`
/// later than `held_since`, in any state that engine can leave (an active or
/// idle rule, or a hysteresis it began before the stale bound).
fn older_engine_state() -> impl Strategy<Value = TrackerState> {
    let kind = prop_oneof![
        Just(TrackerStateKind::Idle),
        Just(TrackerStateKind::Active),
        Just(TrackerStateKind::Hysteresis),
    ];
    (kind, 1i64..=72 * 60, 1i64..=48 * 60).prop_map(|(kind, written_after, held_before)| {
        let held_since = base() - TimeDelta::minutes(held_before);
        let mut updated_at = held_since + TimeDelta::minutes(written_after);
        if kind == TrackerStateKind::Hysteresis {
            updated_at = updated_at.min(held_since + HOLD_BOUND - TimeDelta::minutes(1));
        }
        TrackerState {
            state: kind,
            active_excursion: (kind != TrackerStateKind::Idle).then_some(1),
            last_excursion: 1,
            updated_at,
            hysteresis_started_at: (kind == TrackerStateKind::Hysteresis).then_some(updated_at),
            awaiting_rearm: false,
            held_since: Some(held_since),
        }
    })
}

proptest! {
    /// An excursion closes by the bound only after 24 hours in which every
    /// evaluation this engine made was held, whatever hold an older engine left
    /// stored.
    #[test]
    fn a_stale_stored_hold_never_closes_an_alert_early(
        stored in older_engine_state(),
        steps in steps(),
    ) {
        let rule = low_rule();
        let mut state = restored(stored);
        let mut now = base();
        let mut held_from: Option<DateTime<Utc>> = None;
        for step in &steps {
            now += step.after;
            let e = evaluate_rule(&rule, &context_for(step.root, now), now, &mut state, false)
                .evaluation
                .unwrap();
            if step.root.is_unknown() {
                held_from.get_or_insert(now);
            } else {
                held_from = None;
            }
            if e.transition.close_reason == Some(CloseReason::HoldBound) {
                let since = held_from.expect("closed by the bound on a held evaluation");
                prop_assert!(now - since >= HOLD_BOUND, "closed at {} after a hold from {}", now, since);
            }
        }
    }
}
