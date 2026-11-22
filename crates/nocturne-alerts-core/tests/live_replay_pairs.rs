//! Replay against live evaluation (engine-semantics.md §8): over the same
//! readings, facts and Do Not Disturb, the replay driver fed the ticks a host
//! builds and the live driver stepped as a host steps it, evaluating each
//! reading when it arrives (§5.1, §6.1, §7), open, hold and close the same
//! excursions.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use std::collections::{BTreeSet, HashMap, HashSet};
use std::path::{Path, PathBuf};

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use proptest::prelude::*;
use proptest::sample::select;
use proptest::strategy::ValueTree;
use proptest::test_runner::TestRunner;
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::{ActiveAlertSnapshot, SensorContext, SensorStateSnapshot};
use nocturne_alerts_core::engine::{EngineState, Evaluation, OnUnknown, Rule, evaluate_rule};
use nocturne_alerts_core::enums::{SensorState, WireEnum};
use nocturne_alerts_core::excursion::{
    CloseReason, HOLD_BOUND, TrackerRuleConfig, Transition, TransitionType,
};
use nocturne_alerts_core::facts::{ContextFact, RuleConditions, rule_reads};
use nocturne_alerts_core::model::ConditionKind;
use nocturne_alerts_core::next_change::RESOLUTION;
use nocturne_alerts_core::replay::{ReplayEventKind, ReplayOptions, ReplayTick, replay};
use nocturne_alerts_core::wall_clock::references_wall_clock;

type Event = (DateTime<Utc>, Uuid, ReplayEventKind);

/// How far before its timestamp a reading may be stored and still be a live
/// arrival (`RustAlertEngine.ClockTolerance`, engine-semantics.md §5.4).
const CLOCK_TOLERANCE: TimeDelta = TimeDelta::minutes(5);
/// How often the host samples the other facts in a gap between readings.
const SAMPLE: TimeDelta = TimeDelta::minutes(5);

fn base() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 0, 0, 0).unwrap()
}

/// One reading of the case.
#[derive(Debug, Clone)]
struct Reading {
    at: DateTime<Utc>,
    mgdl: i64,
    /// The connection it came through (`ConnectionKeys`): every stream of one
    /// app, however many grants or phones, is one connection.
    connection: u8,
    /// When Nocturne stored it.
    arrival: DateTime<Utc>,
    /// What the generator made it: history brought in by a connect, which no
    /// live engine evaluated as it came. Only the live side reads this.
    imported: bool,
}

/// Where a host evaluates a reading and the arrival it sends with it.
type Evaluates = fn(&Reading, &[Reading]) -> (DateTime<Utc>, Option<DateTime<Utc>>);

/// The live side: a live reading where it arrived, aged from its arrival; an
/// import where it is dated, aged from that, the closest live history replay
/// can reproduce.
fn as_live(r: &Reading, _: &[Reading]) -> (DateTime<Utc>, Option<DateTime<Utc>>) {
    if r.imported {
        (r.at, None)
    } else {
        (r.arrival, Some(r.arrival))
    }
}

/// The backend's replay (`AlertReplayService.Arrivals`, `ImportedHistory`): a
/// reading dated before its connection first sent anything, or stored further
/// before its timestamp than the clock tolerance, is imported history; every
/// other reading is evaluated at its arrival, however late, and never before
/// its timestamp.
fn as_replay(r: &Reading, all: &[Reading]) -> (DateTime<Utc>, Option<DateTime<Utc>>) {
    let first = all
        .iter()
        .filter(|s| s.connection == r.connection)
        .map(|s| s.arrival)
        .min()
        .unwrap();
    if r.at < first || r.arrival < r.at - CLOCK_TOLERANCE {
        (r.at, None)
    } else {
        (r.arrival.max(r.at), Some(r.arrival))
    }
}

/// The sensor's state a reading carries, read off its value so that both sides
/// see the same state with the same reading: some values come with a state that
/// is not ok, one with ok, and the rest with none.
fn sensor_state_of(mgdl: i64) -> Option<SensorStateSnapshot> {
    let state = match mgdl {
        50 => SensorState::Error,
        75 => SensorState::WarmingUp,
        120 => SensorState::Ok,
        _ => return None,
    };
    Some(SensorStateSnapshot {
        state: Some(state),
        since: None,
        until: None,
    })
}

/// The reading an alert context is built from at `now`: the newest by
/// timestamp of those evaluated by then (engine-semantics.md §4).
fn newest(readings: &[Reading], now: DateTime<Utc>, evaluates: Evaluates) -> Option<&Reading> {
    readings
        .iter()
        .filter(|r| evaluates(r, readings).0 <= now)
        .max_by_key(|r| r.at)
}

#[derive(Debug, Clone)]
struct Case {
    /// An `alert_state` child, when there is one, is listed before its parent.
    rules: Vec<Rule>,
    readings: Vec<Reading>,
    /// IOB set from a host tick on, the tick taken modulo the tick count.
    iob: Vec<(usize, i64)>,
    /// Do Not Disturb over host ticks `[first, first + len)`, the first taken
    /// modulo the tick count, so its edges fall on ticks.
    dnd: Option<(usize, usize)>,
    until: DateTime<Utc>,
}

#[derive(Debug, Clone, Copy)]
struct HostTick {
    at: DateTime<Utc>,
    reading: bool,
}

impl Case {
    fn evaluated(&self, evaluates: Evaluates) -> BTreeSet<DateTime<Utc>> {
        self.readings
            .iter()
            .map(|r| evaluates(r, &self.readings).0)
            .collect()
    }

    /// The ticks the backend's replay builds (`AlertReplayService`): each
    /// reading where it takes live to have evaluated it, and in a gap a sample
    /// every 5 minutes.
    fn host_ticks(&self) -> Vec<HostTick> {
        let evaluated = self.evaluated(as_replay);
        let mut ticks = Vec::new();
        let Some(&first) = evaluated.first() else {
            return ticks;
        };
        let (mut at, mut reading) = (first, true);
        while at < self.until {
            ticks.push(HostTick { at, reading });
            let next = evaluated.range(at + RESOLUTION..).next().copied();
            reading = next.is_some_and(|n| n <= at + SAMPLE);
            at = match next {
                Some(n) if reading => n,
                _ => at + SAMPLE,
            };
        }
        ticks
    }

    /// The IOB and whether Do Not Disturb is on, from each host tick on.
    fn facts(&self, ticks: &[HostTick]) -> Vec<(i64, bool)> {
        let n = ticks.len().max(1);
        let mut set = vec![None; n];
        for &(tick, units) in &self.iob {
            set[tick % n] = Some(units);
        }
        let mut iob = 0;
        (0..ticks.len())
            .map(|i| {
                iob = set[i].unwrap_or(iob);
                let dnd = self.dnd.is_some_and(|(first, len)| {
                    let first = first % n;
                    i >= first && i < first + len
                });
                (iob, dnd)
            })
            .collect()
    }

    fn context(&self, now: DateTime<Utc>, iob: i64, evaluates: Evaluates) -> SensorContext {
        let reading = newest(&self.readings, now, evaluates);
        SensorContext {
            sensor_state: reading.and_then(|r| sensor_state_of(r.mgdl)),
            latest_value: reading.map(|r| Decimal::from(r.mgdl)),
            latest_timestamp: reading.map(|r| r.at),
            latest_received_at: reading.and_then(|r| evaluates(r, &self.readings).1),
            last_reading_at: reading.map(|r| r.at),
            iob_units: Some(Decimal::from(iob)),
            tenant_time_zone_id: Some("UTC".into()),
            ..Default::default()
        }
    }

    fn rule_ids(&self) -> HashSet<Uuid> {
        self.rules.iter().map(|r| r.id).collect()
    }
}

fn replayed(case: &Case) -> Vec<Event> {
    let ticks = case.host_ticks();
    let facts = case.facts(&ticks);
    let ticks = ticks.iter().zip(&facts).map(|(t, &(iob, dnd))| ReplayTick {
        at: t.at,
        context: case.context(t.at, iob, as_replay),
        suppressed_rule_ids: if dnd { case.rule_ids() } else { HashSet::new() },
        reading: t.reading,
    });
    let options = ReplayOptions {
        include_ticks: false,
        until: Some(case.until),
    };
    replay(&case.rules, ticks, options)
        .unwrap()
        .events
        .into_iter()
        .map(|e| (e.at, e.rule_id, e.kind))
        .collect()
}

/// How the live host sweeps between evaluations.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Sweep {
    /// At each rule's next change (§5.3 allows a host this) and at each
    /// sample instant, a sweep the host's 30 s cadence also makes.
    NextChange,
    /// Every 30 s from the first arrival, as the backend's `AlertSweepJob` does.
    Every30s,
    /// Never: a host that evaluates only on writes.
    Never,
}

struct LiveRule<'a> {
    rule: &'a Rule,
    state: EngineState,
    wall_clock: bool,
    reads_insulin: bool,
    reads_alerts: bool,
    due: Option<DateTime<Utc>>,
}

/// The live driver stepped as a host steps it. A reading's arrival evaluates
/// every rule; an IOB write evaluates the rules that read insulin (§5.2); a
/// sweep closes each elapsed hysteresis window (§6.1), then evaluates each
/// wall-clock rule (§5.1). Rules run parents first, against one map of open
/// excursions, so a rule that reads it is evaluated again where a rule before
/// it opened or closed. Each rule keeps its own state, as a host persists it.
struct Live<'a> {
    case: &'a Case,
    ticks: Vec<HostTick>,
    facts: Vec<(i64, bool)>,
    rules: Vec<LiveRule<'a>>,
    active: HashMap<Uuid, ActiveAlertSnapshot>,
    events: Vec<Event>,
}

impl<'a> Live<'a> {
    fn new(case: &'a Case) -> Self {
        let ticks = case.host_ticks();
        let facts = case.facts(&ticks);
        let mut rules: Vec<LiveRule<'a>> = case
            .rules
            .iter()
            .map(|rule| {
                let conditions = RuleConditions::from(rule);
                let reads = rule_reads(&conditions);
                LiveRule {
                    rule,
                    state: EngineState::new(),
                    wall_clock: references_wall_clock(&conditions),
                    reads_insulin: reads.contains(&ContextFact::Insulin),
                    reads_alerts: reads.contains(&ContextFact::ActiveAlerts),
                    due: None,
                }
            })
            .collect();
        rules.sort_by_key(|r| r.reads_alerts);
        Live {
            case,
            ticks,
            facts,
            rules,
            active: HashMap::new(),
            events: Vec::new(),
        }
    }

    /// The facts of the last host tick at or before `at`.
    fn fact_at(&self, at: DateTime<Utc>) -> (i64, bool) {
        let i = self.ticks.partition_point(|t| t.at <= at).saturating_sub(1);
        self.facts.get(i).copied().unwrap_or((0, false))
    }

    fn context(&self, at: DateTime<Utc>) -> SensorContext {
        let mut ctx = self.case.context(at, self.fact_at(at).0, as_live);
        ctx.active_alerts = self.active.clone();
        ctx
    }

    fn record(&mut self, at: DateTime<Utc>, id: Uuid, transition: Transition, resolved: bool) {
        let kind = match (transition.kind, transition.close_reason) {
            (TransitionType::ExcursionOpened, _) => {
                self.active.insert(
                    id,
                    ActiveAlertSnapshot {
                        state: "firing".into(),
                        triggered_at: at,
                        acknowledged_at: None,
                    },
                );
                Some(if self.fact_at(at).1 {
                    ReplayEventKind::SuppressedByDnd
                } else {
                    ReplayEventKind::Fired
                })
            }
            (TransitionType::HysteresisStarted, _) => Some(ReplayEventKind::HysteresisStarted),
            (TransitionType::HysteresisResumed, _) => Some(ReplayEventKind::HysteresisResumed),
            (TransitionType::ExcursionClosed, Some(CloseReason::AutoResolve)) => {
                Some(ReplayEventKind::AutoResolved)
            }
            (TransitionType::ExcursionClosed, _) => Some(ReplayEventKind::Closed),
            _ => None,
        };
        if matches!(transition.kind, TransitionType::ExcursionClosed) {
            self.active.remove(&id);
        }
        self.events.extend(kind.map(|k| (at, id, k)));
        if resolved {
            self.active.remove(&id);
            self.events.push((at, id, ReplayEventKind::AutoResolved));
        }
    }

    fn evaluate(&mut self, i: usize, at: DateTime<Utc>) {
        let ctx = self.context(at);
        let r = &mut self.rules[i];
        let e: Evaluation = evaluate_rule(r.rule, &ctx, at, &mut r.state, false)
            .evaluation
            .unwrap();
        r.due = if r.wall_clock {
            e.next_change_at
        } else {
            sweep_due(r, at)
        };
        let id = r.rule.id;
        self.record(at, id, e.transition, e.auto_resolved);
    }

    fn close(&mut self, i: usize, at: DateTime<Utc>) {
        let r = &mut self.rules[i];
        let config = TrackerRuleConfig {
            hysteresis_minutes: r.rule.hysteresis_minutes,
        };
        let closed = r
            .state
            .tracker
            .close_elapsed_hysteresis(r.rule.id, config, at);
        if !r.wall_clock {
            r.due = sweep_due(r, at);
        }
        let id = r.rule.id;
        self.record(at, id, closed, false);
    }

    /// One instant: each rule in turn is closed, evaluated, both or neither.
    fn instant(
        &mut self,
        at: DateTime<Utc>,
        mut pick: impl FnMut(&LiveRule<'_>, bool) -> (bool, bool),
    ) {
        let mut moved = false;
        for i in 0..self.rules.len() {
            let alerts_moved = moved && self.rules[i].reads_alerts;
            let (close, evaluate) = pick(&self.rules[i], alerts_moved);
            let before = self.active.contains_key(&self.rules[i].rule.id);
            if close {
                self.close(i, at);
            }
            if evaluate || alerts_moved {
                self.evaluate(i, at);
            }
            moved |= before != self.active.contains_key(&self.rules[i].rule.id);
        }
    }

    fn run(mut self, sweep: Sweep) -> Vec<Event> {
        let Some(first) = self.ticks.first().map(|t| t.at) else {
            return Vec::new();
        };
        let arrivals = self.case.evaluated(as_live);
        let writes: BTreeSet<DateTime<Utc>> = self
            .ticks
            .iter()
            .zip(&self.facts)
            .scan(None, |last, (t, &(iob, _))| {
                let moved = last.is_some_and(|l| l != iob);
                *last = Some(iob);
                Some(moved.then_some(t.at))
            })
            .flatten()
            .collect();
        let sweeps: BTreeSet<DateTime<Utc>> = match sweep {
            Sweep::NextChange => self
                .ticks
                .iter()
                .filter(|t| !t.reading)
                .map(|t| t.at)
                .collect(),
            Sweep::Every30s => {
                std::iter::successors(Some(first), |&t| Some(t + TimeDelta::seconds(30)))
                    .take_while(|&t| t < self.case.until)
                    .collect()
            }
            Sweep::Never => BTreeSet::new(),
        };
        let instants: BTreeSet<DateTime<Utc>> = arrivals
            .iter()
            .chain(&writes)
            .chain(&sweeps)
            .copied()
            .filter(|&t| t < self.case.until)
            .collect();
        let mut instants: Vec<DateTime<Utc>> = instants.into_iter().collect();
        instants.push(self.case.until);

        for (n, &at) in instants.iter().enumerate() {
            if sweep == Sweep::NextChange {
                self.step_until(at);
            }
            if n + 1 == instants.len() {
                break;
            }
            if arrivals.contains(&at) {
                self.instant(at, |_, _| (false, true));
            } else {
                let swept = sweeps.contains(&at);
                let wrote = writes.contains(&at);
                self.instant(at, |r, _| {
                    (
                        swept,
                        (swept && (r.wall_clock || hold_due(r, at))) || (wrote && r.reads_insulin),
                    )
                });
            }
        }
        self.events.sort_by_key(|&(at, _, _)| at);
        self.events
    }

    /// Steps each rule due before `limit`, instant by instant: a due rule's
    /// window closes, then a due wall-clock rule, or one whose hold reached
    /// its bound, is evaluated.
    fn step_until(&mut self, limit: DateTime<Utc>) {
        while let Some(at) = self
            .rules
            .iter()
            .filter_map(|r| r.due)
            .filter(|&d| d < limit)
            .min()
        {
            self.instant(at, |r, _| {
                let due = r.due.is_some_and(|d| d <= at);
                (due, due && (r.wall_clock || hold_due(r, at)))
            });
        }
    }
}

/// When the sweep next has to act on a rule it does not evaluate for the
/// clock: its hysteresis window's end, or where its hold reaches the bound,
/// on a sweep after `now`.
fn sweep_due(r: &LiveRule<'_>, now: DateTime<Utc>) -> Option<DateTime<Utc>> {
    let state = r.state.tracker.state(r.rule.id)?;
    let window_end = state
        .hysteresis_started_at
        .map(|started| started + TimeDelta::minutes(i64::from(r.rule.hysteresis_minutes)));
    let bound = state
        .hold_pending()
        .then_some(state.held_since)
        .flatten()
        .map(|since| since + HOLD_BOUND);
    [window_end, bound]
        .into_iter()
        .flatten()
        .min()
        .map(|end| end.max(now + RESOLUTION))
}

/// Whether the backend's sweep evaluates the rule for its hold: held, with
/// the bound reached and not yet reported.
fn hold_due(r: &LiveRule<'_>, at: DateTime<Utc>) -> bool {
    r.state
        .tracker
        .state(r.rule.id)
        .filter(|s| s.hold_pending())
        .and_then(|s| s.held_since)
        .is_some_and(|since| since + HOLD_BOUND <= at)
}

fn live(case: &Case, sweep: Sweep) -> Vec<Event> {
    Live::new(case).run(sweep)
}

/// Per rule, in order: live's events are replay's, each no earlier and at
/// most `slack` later.
fn same_events(replay: &[Event], live: &[Event], slack: TimeDelta) -> Result<(), String> {
    let by_rule = |events: &[Event]| {
        let mut map: HashMap<Uuid, Vec<(DateTime<Utc>, ReplayEventKind)>> = HashMap::new();
        for &(at, rule, kind) in events {
            map.entry(rule).or_default().push((at, kind));
        }
        map
    };
    let (r, l) = (by_rule(replay), by_rule(live));
    let rules: HashSet<&Uuid> = r.keys().chain(l.keys()).collect();
    for rule in rules {
        let (r, l) = (
            r.get(rule).cloned().unwrap_or_default(),
            l.get(rule).cloned().unwrap_or_default(),
        );
        let kinds =
            |e: &[(DateTime<Utc>, ReplayEventKind)]| e.iter().map(|x| x.1).collect::<Vec<_>>();
        if kinds(&r) != kinds(&l) {
            return Err(format!("rule {rule}:\n  replay {r:?}\n  live   {l:?}"));
        }
        for ((ra, kind), (la, _)) in r.iter().zip(&l) {
            if *la < *ra || *la - *ra > slack {
                return Err(format!(
                    "rule {rule}: {kind:?} at {ra} in replay, {la} live\n  replay {r:?}\n  live   {l:?}"
                ));
            }
        }
    }
    Ok(())
}

/// How many more excursions `live` opens than `replay` shows, summed over the
/// rules where it opens more: what a replay would hide.
fn hidden_opens(replay: &[Event], live: &[Event]) -> usize {
    let opens = |events: &[Event]| {
        let mut count: HashMap<Uuid, usize> = HashMap::new();
        for e in events {
            if matches!(
                e.2,
                ReplayEventKind::Fired | ReplayEventKind::SuppressedByDnd
            ) {
                *count.entry(e.1).or_default() += 1;
            }
        }
        count
    };
    let shown = opens(replay);
    opens(live)
        .into_iter()
        .map(|(rule, n)| n.saturating_sub(shown.get(&rule).copied().unwrap_or(0)))
        .sum()
}

fn threshold(direction: &str, value: i64) -> Value {
    json!({ "type": "threshold", "threshold": { "direction": direction, "value": value } })
}

fn body() -> impl Strategy<Value = Value> {
    let level = select(vec![70i64, 80, 180]);
    let leaf = (select(vec!["below", "above"]), level.clone()).prop_map(|(d, v)| threshold(d, v));
    let margined =
        (select(vec!["below", "above"]), level.clone(), 1i64..=20).prop_map(|(d, v, m)| {
            json!({ "type": "threshold",
                "threshold": { "direction": d, "value": v, "clear_margin": m } })
        });
    prop_oneof![
        3 => leaf.clone(),
        2 => margined.clone(),
        1 => margined.prop_map(|child| json!({ "type": "not", "not": { "child": child } })),
        2 => (1i64..=20, leaf.clone()).prop_map(|(m, child)| json!({
            "type": "sustained", "sustained": { "minutes": m, "child": child } })),
        2 => (5i64..=40).prop_map(|m| json!({
            "type": "signal_loss", "signal_loss": { "timeout_minutes": m } })),
        1 => (5i64..=40).prop_map(|m| json!({
            "type": "staleness", "staleness": { "operator": ">=", "value": m } })),
        2 => (leaf.clone(), 0u32..24, 1u32..24).prop_map(|(child, from, len)| json!({
            "type": "composite", "composite": { "operator": "and", "conditions": [
                child,
                { "type": "time_of_day", "time_of_day": {
                    "from": format!("{from:02}:00"),
                    "to": format!("{:02}:30", (from + len) % 24) } }
            ] } })),
        1 => select(vec!["error", "warming_up", "ok"]).prop_map(|state| json!({
            "type": "sensor_state", "sensor_state": { "states": [state] } })),
        2 => (leaf, 5i64..=40).prop_map(|(child, m)| json!({
            "type": "composite", "composite": { "operator": "or", "conditions": [
                child,
                { "type": "signal_loss", "signal_loss": { "timeout_minutes": m } }
            ] } })),
    ]
}

fn rule_from(n: u128, node: &Value) -> Rule {
    let kind = ConditionKind::from_wire(node["type"].as_str().unwrap()).unwrap();
    Rule {
        id: Uuid::from_u128(n),
        condition_type: kind,
        condition_params: node[kind.name()].clone(),
        hysteresis_minutes: 0,
        auto_resolve_enabled: false,
        auto_resolve_params: None,
        on_unknown: OnUnknown::Hold,
        glucose_freshness: TimeDelta::minutes(11),
        enabled_at: None,
    }
}

fn rule_of(n: u128) -> impl Strategy<Value = Rule> {
    (
        body(),
        select(vec![0, 0, 5, 10, 15, 30]),
        prop_oneof![
            2 => Just(None),
            1 => (select(vec!["below", "above"]), select(vec![70i64, 100, 180]))
                .prop_map(|(d, v)| Some(threshold(d, v))),
            1 => (1i64..=15, select(vec![70i64, 100, 180])).prop_map(|(m, v)| Some(json!({
                "type": "sustained", "sustained": { "minutes": m,
                    "child": threshold("above", v) } }))),
        ],
        select(vec![OnUnknown::Hold, OnUnknown::Met, OnUnknown::NotMet]),
        select(vec![5i64, 11, 20]),
    )
        .prop_map(
            move |(node, hysteresis, auto_resolve, on_unknown, fresh)| Rule {
                hysteresis_minutes: hysteresis,
                auto_resolve_enabled: auto_resolve.is_some(),
                auto_resolve_params: auto_resolve,
                on_unknown,
                glucose_freshness: TimeDelta::minutes(fresh),
                ..rule_from(n, &node)
            },
        )
}

/// An IOB rule, read by the samples and the IOB writes.
fn iob_rule() -> impl Strategy<Value = Rule> {
    (1i64..=4, select(vec![0, 5, 10])).prop_map(|(units, hysteresis)| Rule {
        hysteresis_minutes: hysteresis,
        ..rule_from(
            4,
            &json!({ "type": "iob", "iob": { "operator": ">", "value": units } }),
        )
    })
}

/// A rule over rule 1's open excursion, alone or with a reading.
fn child_rule() -> impl Strategy<Value = Rule> {
    let parent = Uuid::from_u128(1).to_string();
    let state = move |for_minutes: Option<i64>| {
        let mut payload = json!({ "alert_id": parent, "state": "firing" });
        if let Some(m) = for_minutes {
            payload["for_minutes"] = json!(m);
        }
        json!({ "type": "alert_state", "alert_state": payload })
    };
    (proptest::option::of(0i64..=15), proptest::bool::ANY).prop_map(
        move |(for_minutes, with_low)| {
            let node = if with_low {
                json!({ "type": "composite", "composite": { "operator": "and",
                "conditions": [state(for_minutes), threshold("below", 80)] } })
            } else {
                state(for_minutes)
            };
            rule_from(5, &node)
        },
    )
}

fn rules() -> impl Strategy<Value = Vec<Rule>> {
    (
        proptest::collection::vec(1u128..=3, 1..=3),
        proptest::option::weighted(0.3, iob_rule()),
        proptest::option::weighted(0.4, child_rule()),
    )
        .prop_flat_map(|(ids, iob, child)| {
            let mut ids = ids;
            ids.push(1);
            ids.sort_unstable();
            ids.dedup();
            let parents: Vec<_> = ids.into_iter().map(rule_of).collect();
            (Just((iob, child)), parents)
        })
        .prop_map(|((iob, child), parents)| child.into_iter().chain(parents).chain(iob).collect())
}

/// From a live reading's timestamp to its arrival: on time, late, and caught
/// up after an outage well past a signal-loss timeout.
fn delay() -> impl Strategy<Value = TimeDelta> {
    prop_oneof![
        6 => (0i64..=180).prop_map(TimeDelta::seconds),
        3 => (180i64..=900).prop_map(TimeDelta::seconds),
        2 => (900i64..=2400).prop_map(TimeDelta::seconds),
        1 => (2400i64..=7200).prop_map(TimeDelta::seconds),
    ]
}

fn case() -> impl Strategy<Value = Case> {
    let gap = prop_oneof![
        8 => Just(300i64),
        2 => 60i64..=600,
        1 => 600i64..=7200,
    ];
    let value = select(vec![50i64, 65, 75, 90, 120, 170, 190, 250]);
    let imports = proptest::option::weighted(
        0.4,
        (
            proptest::collection::vec((gap.clone(), value.clone()), 1..40),
            0i64..=60,
        ),
    );
    // Another stream of the same connection (the app authorised again, or a
    // new phone), whose first upload is the backlog it could not send.
    let second_stream = proptest::option::weighted(
        0.4,
        (
            600i64..=18_000,
            proptest::collection::vec(value.clone(), 2..12),
            1usize..=6,
            0i64..=3600,
        ),
    );
    // Another connection (a different app), its first reading on time.
    let other_connection = proptest::option::weighted(
        0.3,
        (
            600i64..=18_000,
            proptest::collection::vec((value.clone(), delay()), 2..12),
        ),
    );
    (
        0i64..86_400,
        imports,
        proptest::collection::vec((gap, value, delay()), 4..60),
        (second_stream, other_connection),
        rules(),
        proptest::collection::vec((0usize..200, 0i64..=6), 0..6),
        proptest::option::weighted(0.3, (0usize..200, 1usize..20)),
        0i64..=7200,
    )
        .prop_map(
            |(start, imports, steps, (second, other), rules, iob, dnd, tail)| {
                // A connect brings in the history before it, all stored at once,
                // then the stream sends live. Its first reading is on time unless
                // the connection already sent the history.
                let connect = base() + TimeDelta::seconds(start);
                let mut readings = Vec::new();
                let mut at = connect;
                if let Some((history, jitter)) = &imports {
                    let mut before = connect - TimeDelta::minutes(10);
                    for &(gap, mgdl) in history {
                        readings.push(Reading {
                            at: before,
                            mgdl,
                            arrival: connect + TimeDelta::seconds(*jitter),
                            imported: true,
                            connection: 0,
                        });
                        before -= TimeDelta::seconds(gap);
                    }
                    readings.reverse();
                    at += TimeDelta::minutes(5);
                }
                for (n, (gap, mgdl, delay)) in steps.into_iter().enumerate() {
                    if n > 0 {
                        at += TimeDelta::seconds(gap);
                    }
                    let delay = if n == 0 && imports.is_none() {
                        TimeDelta::zero()
                    } else {
                        delay
                    };
                    readings.push(Reading {
                        at,
                        mgdl,
                        connection: 0,
                        arrival: at + delay,
                        imported: false,
                    });
                }
                let five = TimeDelta::minutes(5);
                if let Some((offset, values, backlog, late)) = second {
                    let begin = connect + TimeDelta::seconds(offset);
                    let caught_up = begin
                        + five * (i32::try_from(backlog).unwrap() - 1)
                        + TimeDelta::seconds(late);
                    for (n, mgdl) in values.into_iter().enumerate() {
                        let at = begin + five * i32::try_from(n).unwrap();
                        let arrival = if n < backlog { caught_up } else { at };
                        readings.push(Reading {
                            at,
                            mgdl,
                            connection: 0,
                            arrival: arrival.max(at),
                            imported: false,
                        });
                    }
                }
                if let Some((offset, values)) = other {
                    let begin = connect + TimeDelta::seconds(offset);
                    for (n, (mgdl, delay)) in values.into_iter().enumerate() {
                        let at = begin + five * i32::try_from(n).unwrap();
                        let delay = if n == 0 { TimeDelta::zero() } else { delay };
                        readings.push(Reading {
                            at,
                            mgdl,
                            connection: 1,
                            arrival: at + delay,
                            imported: false,
                        });
                    }
                }
                let at = readings.iter().map(|r| r.at).max().unwrap();
                let last = readings.iter().map(|r| r.arrival.max(r.at)).max().unwrap();
                Case {
                    rules,
                    readings,
                    iob,
                    dnd,
                    until: last.max(at) + TimeDelta::seconds(tail) + RESOLUTION,
                }
            },
        )
}

impl Case {
    fn check(&self, sweep: Sweep) -> Result<(), String> {
        let slack = match sweep {
            Sweep::Every30s => TimeDelta::seconds(60),
            Sweep::NextChange | Sweep::Never => TimeDelta::zero(),
        };
        same_events(&replayed(self), &live(self, sweep), slack)
    }
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 512, ..ProptestConfig::default() })]

    #[test]
    fn replay_opens_and_closes_what_live_evaluation_does(case in case()) {
        if let Err(e) = case.check(Sweep::NextChange) {
            prop_assert!(false, "{e}\n{case:#?}");
        }
    }
}

/// One rule over live readings `(minute, mg/dL, upload delay in minutes)`,
/// ending at `until` minutes.
fn fixed(rule: Rule, readings: &[(i64, i64, i64)], until: i64) -> Case {
    let at = |m: i64| base() + TimeDelta::minutes(m);
    fixed_with(
        rule,
        readings
            .iter()
            .map(|&(m, mgdl, delay)| Reading {
                at: at(m),
                mgdl,
                arrival: at(m + delay),
                imported: false,
                connection: 0,
            })
            .collect(),
        until,
    )
}

fn fixed_with(rule: Rule, readings: Vec<Reading>, until: i64) -> Case {
    Case {
        rules: vec![rule],
        readings,
        iob: Vec::new(),
        dnd: None,
        until: base() + TimeDelta::minutes(until),
    }
}

/// Readings `(minute, mg/dL)` brought in by a connect at `connect` minutes.
fn imported(readings: &[(i64, i64)], connect: i64) -> Vec<Reading> {
    readings
        .iter()
        .map(|&(m, mgdl)| Reading {
            at: base() + TimeDelta::minutes(m),
            mgdl,
            arrival: base() + TimeDelta::minutes(connect),
            imported: true,
            connection: 0,
        })
        .collect()
}
fn fixed_rule(node: Value) -> Rule {
    rule_from(1, &node)
}

fn at_minutes(events: &[Event]) -> Vec<(i64, ReplayEventKind)> {
    events
        .iter()
        .map(|&(at, _, kind)| ((at - base()).num_minutes(), kind))
        .collect()
}

#[test]
fn fixed_cases_pair() {
    let every5 = |values: &[i64]| -> Vec<(i64, i64, i64)> {
        (0i64..)
            .step_by(5)
            .zip(values.iter().copied())
            .map(|(m, v)| (m, v, 0))
            .collect()
    };
    let low = threshold("below", 70);

    let mut hysteresis = fixed_rule(low.clone());
    hysteresis.hysteresis_minutes = 15;

    let sustained = fixed_rule(json!({
        "type": "sustained", "sustained": { "minutes": 15, "child": low } }));

    let mut rearm = fixed_rule(low.clone());
    rearm.auto_resolve_enabled = true;
    rearm.auto_resolve_params = Some(threshold("below", 100));

    let gap = [(0, 60, 0), (5, 60, 0), (60, 60, 0), (65, 90, 0)];
    let on_unknown = [OnUnknown::Hold, OnUnknown::Met, OnUnknown::NotMet].map(|word| {
        let mut r = fixed_rule(low.clone());
        r.on_unknown = word;
        r.hysteresis_minutes = 10;
        fixed(r, &gap, 90)
    });

    let signal_loss = fixed_rule(json!({
        "type": "signal_loss", "signal_loss": { "timeout_minutes": 20 } }));
    let night_low = fixed_rule(json!({
        "type": "composite", "composite": { "operator": "and", "conditions": [
            low, { "type": "time_of_day", "time_of_day": { "from": "00:30", "to": "01:00" } }
        ] } }));

    let cases = [
        fixed(hysteresis, &every5(&[60, 80, 60, 80, 80, 80, 80]), 60),
        fixed(
            sustained,
            &every5(&[60, 60, 60, 60, 80, 60, 60, 60, 60]),
            60,
        ),
        fixed(rearm, &every5(&[60, 60, 80, 60, 120, 60]), 40),
        fixed(signal_loss, &[(0, 100, 0), (45, 100, 0), (50, 100, 0)], 120),
        fixed(night_low, &[(25, 60, 0), (40, 60, 0), (70, 60, 0)], 90),
    ];
    for case in cases.iter().chain(&on_unknown) {
        case.check(Sweep::NextChange)
            .unwrap_or_else(|e| panic!("{e}\n{case:#?}"));
        assert!(
            replayed(case).iter().any(|e| e.2 == ReplayEventKind::Fired),
            "a fixed case that opens nothing proves nothing: {case:#?}"
        );
    }
}

fn signal_loss(minutes: i64) -> Rule {
    fixed_rule(json!({
        "type": "signal_loss", "signal_loss": { "timeout_minutes": minutes } }))
}

/// Readings at 00:00 and 00:17, each uploaded 4 minutes late after one on time
/// at 23:55: live measures the outage to each arrival, so a 20-minute signal
/// loss fires at 00:20, before the second reading arrives.
#[test]
fn signal_loss_over_an_upload_delay_fires_as_live_does() {
    let case = fixed(
        signal_loss(20),
        &[(-5, 100, 0), (0, 100, 4), (17, 100, 4)],
        30,
    );
    case.check(Sweep::NextChange).unwrap();
    assert_eq!(
        at_minutes(&replayed(&case)),
        vec![
            (20, ReplayEventKind::Fired),
            (21, ReplayEventKind::HysteresisStarted),
            (21, ReplayEventKind::Closed),
        ]
    );
}

/// The uploader goes quiet after 00:30 and catches up at 00:52 with the
/// readings for 00:35 to 00:50, up to 17 minutes late. Live saw the outage
/// until the catch-up, so a 20-minute signal loss fires at 00:50 and ends at
/// 00:52.
#[test]
fn a_catch_up_after_an_outage_shows_the_signal_loss_live_raised() {
    let readings: Vec<_> = (0..=10)
        .map(|i| {
            let m = 5 * i;
            (m, 100, if m <= 30 { 0 } else { 52 - m })
        })
        .collect();
    let case = fixed(signal_loss(20), &readings, 60);
    case.check(Sweep::NextChange).unwrap();
    assert_eq!(
        at_minutes(&replayed(&case)),
        vec![
            (50, ReplayEventKind::Fired),
            (52, ReplayEventKind::HysteresisStarted),
            (52, ReplayEventKind::Closed),
        ]
    );
}

/// Readings `(minute, mg/dL, arrival minute)` of one connection.
fn of_connection(connection: u8, readings: &[(i64, i64, i64)]) -> Vec<Reading> {
    readings
        .iter()
        .map(|&(m, mgdl, arrival)| Reading {
            at: base() + TimeDelta::minutes(m),
            mgdl,
            connection,
            arrival: base() + TimeDelta::minutes(arrival),
            imported: false,
        })
        .collect()
}

/// The app sends on time until 00:30, is authorised again, and its new grant
/// uploads the readings for 00:35 to 00:50 at 00:52, then sends on time. Both
/// grants are one connection, so the backlog is a late arrival: live saw the
/// outage until 00:52, and a 20-minute signal loss fires at 00:50.
#[test]
fn a_backlog_after_the_app_is_authorised_again_shows_the_signal_loss() {
    let mut readings = of_connection(
        0,
        &[
            (0, 100, 0),
            (5, 100, 5),
            (10, 100, 10),
            (15, 100, 15),
            (20, 100, 20),
            (25, 100, 25),
            (30, 100, 30),
        ],
    );
    readings.extend(of_connection(
        0,
        &[
            (35, 100, 52),
            (40, 100, 52),
            (45, 100, 52),
            (50, 100, 52),
            (55, 100, 55),
            (60, 100, 60),
        ],
    ));
    let case = fixed_with(signal_loss(20), readings, 70);
    case.check(Sweep::NextChange).unwrap();
    assert_eq!(
        at_minutes(&replayed(&case)),
        vec![
            (50, ReplayEventKind::Fired),
            (52, ReplayEventKind::HysteresisStarted),
            (52, ReplayEventKind::Closed),
        ]
    );
}

/// A reader clamped to the last 24 hours, its edge at minute 100: an on-time
/// reading at 95, an outage, then a catch-up at 142 of the readings for 100 to
/// 140. The connection's first arrival is read with the clamp lifted, so it is
/// the owner's (minute 0) and the catch-up is a late arrival: the clamped
/// reader's replay shows the 20-minute signal loss at 115 as the owner's does.
/// With the first arrival read under the clamp (142) the catch-up would be
/// imported history and the alert would not be shown.
#[test]
fn a_catch_up_across_the_clamps_edge_shows_the_signal_loss() {
    let mut readings = of_connection(0, &[(0, 100, 0), (95, 100, 95)]);
    readings.extend(of_connection(
        0,
        &[
            (100, 100, 142),
            (110, 100, 142),
            (120, 100, 142),
            (130, 100, 142),
            (140, 100, 142),
            (145, 100, 145),
        ],
    ));
    let owner = fixed_with(signal_loss(20), readings.clone(), 150);
    owner.check(Sweep::NextChange).unwrap();
    assert!(at_minutes(&replayed(&owner)).contains(&(115, ReplayEventKind::Fired)));

    let under_the_clamp = fixed_with(
        signal_loss(20),
        readings
            .into_iter()
            .filter(|r| r.at >= base() + TimeDelta::minutes(100))
            .collect(),
        150,
    );
    assert!(
        !replayed(&under_the_clamp)
            .iter()
            .any(|e| e.2 == ReplayEventKind::Fired),
        "read under the clamp, the catch-up is taken for imported history"
    );
}

/// The same backlog from a different app, a new connection: dated before that
/// connection first sent anything, it is replayed as imported history, so the
/// outage live raised at 00:50 is not shown (engine-semantics.md §8).
#[test]
fn a_new_connections_first_backlog_is_imported_history() {
    let mut readings = of_connection(
        0,
        &[(0, 100, 0), (10, 100, 10), (20, 100, 20), (30, 100, 30)],
    );
    readings.extend(of_connection(
        1,
        &[
            (35, 100, 52),
            (40, 100, 52),
            (45, 100, 52),
            (50, 100, 52),
            (55, 100, 55),
        ],
    ));
    let case = fixed_with(signal_loss(20), readings, 60);
    assert!(at_minutes(&replayed(&case)).is_empty());
    assert_eq!(
        at_minutes(&live(&case, Sweep::NextChange))[0],
        (50, ReplayEventKind::Fired),
        "live raised it"
    );
}

/// A first connect at 02:00 brings in readings from 00:00 to 01:55, with none
/// from 00:30 to 01:05, all stored at the connect, then the stream sends live.
/// The history is dated before the connection's first arrival, so it is replayed
/// at its timestamps: a 30-minute signal loss fires in its gap, at 00:55, and
/// nowhere else until the live readings stop.
#[test]
fn a_first_connect_backfill_reads_its_gaps_from_its_timestamps() {
    let history: Vec<_> = (0..24)
        .map(|i| (5 * i, 100))
        .filter(|&(m, _)| !(30..=65).contains(&m))
        .collect();
    let mut readings = imported(&history, 120);
    readings.extend(fixed(signal_loss(30), &[(125, 100, 0), (130, 100, 0)], 0).readings);
    let case = fixed_with(signal_loss(30), readings, 180);
    case.check(Sweep::NextChange).unwrap();
    assert_eq!(
        at_minutes(&replayed(&case)),
        vec![
            (55, ReplayEventKind::Fired),
            (70, ReplayEventKind::HysteresisStarted),
            (70, ReplayEventKind::Closed),
            (160, ReplayEventKind::Fired),
        ]
    );
}

/// A day brought in by a connect a day later is dated before the stream's
/// first arrival, so each reading ages from its timestamp and the hour-long
/// gap reads stale under each `on_unknown`: `met` fires there and again once
/// the last reading is stale, `not_met` ends the excursion and the low after
/// it opens another, `hold` keeps it open across the gap.
#[test]
fn a_backfilled_day_goes_stale_across_a_gap() {
    let fires = |word: OnUnknown, node: Value, before: i64, after: i64| {
        let mut rule = fixed_rule(node);
        rule.on_unknown = word;
        let readings = imported(&[(0, before), (5, before), (65, after), (70, 100)], 1440);
        let case = fixed_with(rule, readings, 90);
        case.check(Sweep::NextChange).unwrap();
        replayed(&case)
            .into_iter()
            .filter(|e| e.2 == ReplayEventKind::Fired)
            .map(|e| e.0 - base())
            .collect::<Vec<_>>()
    };
    let past = |m: i64| TimeDelta::minutes(m) + RESOLUTION;
    assert_eq!(
        fires(OnUnknown::Met, threshold("above", 200), 60, 60),
        vec![past(16), past(81)]
    );
    assert_eq!(
        fires(OnUnknown::NotMet, threshold("below", 70), 60, 60),
        vec![TimeDelta::zero(), TimeDelta::minutes(65)]
    );
    assert_eq!(
        fires(OnUnknown::Hold, threshold("below", 70), 60, 60),
        vec![TimeDelta::zero()]
    );
}

fn sample(n: usize) -> Vec<Case> {
    let mut runner = TestRunner::deterministic();
    (0..n)
        .map(|_| case().new_tree(&mut runner).unwrap().current())
        .collect()
}

/// The pairing is not vacuous, and it sees a host that does not sweep. Against
/// a host that sweeps every 30 s, replay shows far more openings than it hides.
/// It hides one through the sweep's phase alone (engine-semantics.md §8): a
/// change less than 30 s before a hysteresis window ends, which replay resumes
/// and the sweep closes and opens again; a wall-clock rule's not-true instants,
/// recorded at each sweep live and at each change in replay, which move a
/// sustained run's start; and an `alert_state` child's `for_minutes`, restarted
/// where replay opens its parent again.
#[test]
fn the_pairing_opens_and_closes_excursions_and_needs_the_sweep() {
    #[derive(Debug, Default)]
    struct Tally {
        cases: usize,
        opened: usize,
        closed: usize,
        resolved: usize,
        resumed: usize,
        between: usize,
        late: usize,
        imported: usize,
        caught_up: usize,
        backlog: usize,
        other_connection: usize,
        child: usize,
        iob: usize,
        suppressed: usize,
        unswept: usize,
        cadence: usize,
        cadence_hides: usize,
        cadence_hidden_opens: usize,
        cadence_extra_opens: usize,
    }
    let mut t = Tally::default();
    for case in &sample(512) {
        let events = replayed(case);
        let has = |kind| events.iter().any(|e| e.2 == kind);
        let fired_by = |n: u128| {
            events
                .iter()
                .any(|e| e.1 == Uuid::from_u128(n) && e.2 == ReplayEventKind::Fired)
        };
        let ticks: HashSet<_> = case.host_ticks().iter().map(|t| t.at).collect();
        t.cases += 1;
        t.opened += usize::from(has(ReplayEventKind::Fired));
        t.closed += usize::from(has(ReplayEventKind::Closed));
        t.resolved += usize::from(has(ReplayEventKind::AutoResolved));
        t.resumed += usize::from(has(ReplayEventKind::HysteresisResumed));
        t.suppressed += usize::from(has(ReplayEventKind::SuppressedByDnd));
        t.between += usize::from(events.iter().any(|e| !ticks.contains(&e.0)));
        t.late += usize::from(
            case.readings
                .iter()
                .any(|r| !r.imported && r.arrival - r.at > TimeDelta::minutes(3)),
        );
        t.imported += usize::from(case.readings.iter().any(|r| r.imported));
        t.backlog += usize::from(case.readings.iter().any(|r| {
            !r.imported
                && r.arrival > r.at
                && case
                    .readings
                    .iter()
                    .filter(|s| s.arrival == r.arrival && !s.imported)
                    .count()
                    > 1
        }));
        t.other_connection += usize::from(case.readings.iter().any(|r| r.connection == 1));
        t.caught_up += usize::from(
            case.readings
                .iter()
                .any(|r| !r.imported && r.arrival - r.at > TimeDelta::minutes(15)),
        );
        t.child += usize::from(fired_by(5));
        t.iob += usize::from(fired_by(4));
        t.unswept += usize::from(case.check(Sweep::Never).is_err());
        t.cadence += usize::from(case.check(Sweep::Every30s).is_err());
        let every30s = live(case, Sweep::Every30s);
        let hidden = hidden_opens(&events, &every30s);
        t.cadence_hides += usize::from(hidden > 0);
        t.cadence_hidden_opens += hidden;
        t.cadence_extra_opens += hidden_opens(&every30s, &events);
    }
    eprintln!("{t:#?}");
    assert!(t.opened * 2 >= t.cases, "{t:?}");
    assert!(t.closed * 3 >= t.cases, "{t:?}");
    assert!(t.resolved * 10 >= t.cases, "{t:?}");
    assert!(t.resumed * 20 >= t.cases, "{t:?}");
    assert!(t.between * 4 >= t.cases, "{t:?}");
    assert!(t.late * 4 >= t.cases, "{t:?}");
    assert!(t.imported * 4 >= t.cases, "{t:?}");
    assert!(t.caught_up * 4 >= t.cases, "{t:?}");
    assert!(t.backlog * 5 >= t.cases, "{t:?}");
    assert!(t.other_connection * 5 >= t.cases, "{t:?}");
    assert!(t.child * 10 >= t.cases, "{t:?}");
    assert!(t.iob * 20 >= t.cases, "{t:?}");
    assert!(t.suppressed * 20 >= t.cases, "{t:?}");
    assert!(t.unswept * 4 >= t.cases, "{t:?}");
    assert!(t.cadence_hides * 20 <= t.cases, "{t:?}");
    assert!(t.cadence_hidden_opens * 4 <= t.cadence_extra_opens, "{t:?}");
}

fn corpus() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/Parity/AlertEngineCorpus")
}

fn read(path: &Path) -> Value {
    serde_json::from_str(&std::fs::read_to_string(path).expect("read scenario"))
        .expect("parse scenario")
}

/// The live snapshot's transitions, which its ticks evaluate with no sweep
/// between them.
fn snapshot_events(name: &str) -> Vec<(String, String, String)> {
    let expected = read(&corpus().join(format!("{name}.expected.json")));
    let mut events = Vec::new();
    for tick in expected["ticks"].as_array().unwrap() {
        let at = tick["at"].as_str().unwrap().to_owned();
        for rule in tick["rules"].as_array().unwrap() {
            let id = rule["rule_id"].as_str().unwrap().to_owned();
            let kind = match (rule["transition"].as_str(), rule["close_reason"].as_str()) {
                (Some("opened"), _) => Some("fired"),
                (Some("closed"), Some("auto")) => Some("auto_resolved"),
                (Some(kind @ ("hysteresis_started" | "hysteresis_resumed" | "closed")), _) => {
                    Some(kind)
                }
                _ => None,
            };
            if let Some(kind) = kind {
                events.push((at.clone(), id.clone(), kind.to_owned()));
            }
            if rule["auto_resolved"] == Value::Bool(true) {
                events.push((at.clone(), id, "auto_resolved".to_owned()));
            }
        }
    }
    events
}

/// The replay snapshot's events at its ticks; a step between ticks is a sweep
/// the live snapshot does not run.
fn replay_snapshot_events(path: &Path, ticks: &HashSet<String>) -> Vec<(String, String, String)> {
    read(path)["events"]
        .as_array()
        .unwrap()
        .iter()
        .map(|e| {
            let kind = match e["kind"].as_str().unwrap() {
                "suppressed_by_dnd" => "fired",
                other => other,
            };
            (
                e["at"].as_str().unwrap().to_owned(),
                e["rule_id"].as_str().unwrap().to_owned(),
                kind.to_owned(),
            )
        })
        .filter(|e| ticks.contains(&e.0))
        .collect()
}

/// A replay scenario `replay-<name>` beside a live scenario `<name>` feeds the
/// same rules the same readings: every transition the live snapshot makes on
/// a tick, replay makes on that tick, except a close the sweep made earlier.
#[test]
fn paired_live_and_replay_scenarios_yield_the_same_transitions() {
    let mut pairs = 0;
    for entry in std::fs::read_dir(corpus().join("replay")).unwrap() {
        let path = entry.unwrap().path();
        let file = path.file_name().unwrap().to_str().unwrap();
        let Some(name) = file
            .strip_suffix(".expected.json")
            .and_then(|n| n.strip_prefix("replay-"))
        else {
            continue;
        };
        if !corpus().join(format!("{name}.expected.json")).exists() {
            continue;
        }
        pairs += 1;
        let ticks = assert_same_inputs(name);
        let replay = replay_snapshot_events(&path, &ticks);
        let live: Vec<_> = snapshot_events(name)
            .into_iter()
            .filter(|e| e.2 != "closed" || replay.contains(e))
            .collect();
        assert_eq!(live, replay, "live {name} and replay-{name} diverge");
    }
    assert!(
        pairs >= 2,
        "expected at least two paired scenarios, found {pairs}"
    );
}

/// A pair pins one behaviour only while both scenarios feed the same rules the
/// same readings at the same instants. The tick instants.
fn assert_same_inputs(name: &str) -> HashSet<String> {
    let live = read(&corpus().join(format!("{name}.json")));
    let replay = read(&corpus().join("replay").join(format!("replay-{name}.json")));
    assert_eq!(
        live["rules"], replay["rules"],
        "{name}: paired rules differ"
    );
    let ticks = |s: &Value| -> Vec<(Value, Value)> {
        s["ticks"]
            .as_array()
            .unwrap()
            .iter()
            .map(|t| (t["at"].clone(), t["context"].clone()))
            .collect()
    };
    assert_eq!(ticks(&live), ticks(&replay), "{name}: paired ticks differ");
    ticks(&live)
        .into_iter()
        .map(|(at, _)| at.as_str().unwrap().to_owned())
        .collect()
}
