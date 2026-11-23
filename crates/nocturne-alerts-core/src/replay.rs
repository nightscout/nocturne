//! The replay driver (engine-semantics.md §8): a rule set stepped over a
//! series of pre-enriched ticks through the live driver sequence
//! ([`evaluate_parsed_rule`]), with sustained timers and the excursion
//! tracker held in memory for the call.

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use crate::context::{ActiveAlertSnapshot, SensorContext};
use crate::engine::{EngineState, Evaluation, Rule, evaluate_parsed_rule, format_instant};
use crate::excursion::{
    CloseReason, TrackerRuleConfig, TrackerState, TrackerStateKind, TransitionType,
};
use crate::facts::{ContextFact, RuleConditions, rule_reads};
use crate::leaf_identity::collect_leaves;
use crate::model::{Node, Payload};
use crate::next_change::next_change_at;
use crate::truth::Truth;
use crate::wall_clock::references_wall_clock;

/// How many times one rule may be stepped between two ticks, or after the last
/// tick. With every fact frozen a rule changes only where an elapsed time
/// crosses a bound, once each, or at a calendar edge, a few a day, so a
/// replay that reaches it is reported rather than cut short.
pub const MAX_STEPS_PER_GAP: usize = 1_000;

/// One replay tick: the instant and the context enriched as of it.
#[derive(Debug, Clone, Deserialize)]
pub struct ReplayTick {
    pub at: DateTime<Utc>,
    /// Its `active_alerts` are replaced by the replay's own.
    pub context: SensorContext,
    /// The rules a fire opening on this tick, or on a step before the next
    /// tick, is recorded as suppressed for by Do Not Disturb, which the host
    /// resolves.
    #[serde(default)]
    pub suppressed_rule_ids: HashSet<Uuid>,
    /// A glucose reading arrived at `at`, so every rule is evaluated, as live
    /// evaluates every rule per reading. Otherwise the tick samples the other
    /// facts between readings and evaluates only the rules a live host would
    /// evaluate then (engine-semantics.md §8).
    #[serde(default = "a_reading")]
    pub reading: bool,
}

fn a_reading() -> bool {
    true
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum ReplayEventKind {
    /// An excursion opened.
    Fired,
    /// An excursion opened while Do Not Disturb would have kept it silent.
    SuppressedByDnd,
    HysteresisStarted,
    HysteresisResumed,
    /// The excursion closed when its hysteresis window ran out.
    Closed,
    AutoResolved,
}

impl ReplayEventKind {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            ReplayEventKind::Fired => "fired",
            ReplayEventKind::SuppressedByDnd => "suppressed_by_dnd",
            ReplayEventKind::HysteresisStarted => "hysteresis_started",
            ReplayEventKind::HysteresisResumed => "hysteresis_resumed",
            ReplayEventKind::Closed => "closed",
            ReplayEventKind::AutoResolved => "auto_resolved",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReplayEvent {
    pub at: DateTime<Utc>,
    pub rule_id: Uuid,
    pub kind: ReplayEventKind,
}

/// A rule's state after one evaluation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ReplayRuleState {
    pub met: Truth,
    /// An excursion is open, active or in hysteresis.
    pub firing: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReplayRuleTick {
    pub rule_id: Uuid,
    /// `None` when the body cannot be evaluated: the rule is skipped.
    pub state: Option<ReplayRuleState>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReplayTickOutcome {
    pub at: DateTime<Utc>,
    /// The rules evaluated at `at`, in evaluation order.
    pub rules: Vec<ReplayRuleTick>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LeafPoint {
    pub at: DateTime<Utc>,
    pub value: Truth,
}

/// A rule's leaf log: per leaf id, the first observation then every flip.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuleLeafLog {
    pub rule_id: Uuid,
    pub leaves: Vec<Vec<LeafPoint>>,
}

#[derive(Debug, Clone, Copy, Default)]
pub struct ReplayOptions {
    /// Report every evaluation instant and the rules evaluated there.
    pub include_ticks: bool,
    /// Step the rules on past the last tick up to, not including, this
    /// instant: the end of the replayed window.
    pub until: Option<DateTime<Utc>>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReplayOutcome {
    /// Rule ids in evaluation order.
    pub order: Vec<Uuid>,
    /// In the order they happened: by instant, then evaluation order.
    pub events: Vec<ReplayEvent>,
    /// In evaluation order; a rule skipped on every tick has none.
    pub leaf_transitions: Vec<RuleLeafLog>,
    /// Present when [`ReplayOptions::include_ticks`].
    pub ticks: Option<Vec<ReplayTickOutcome>>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
#[non_exhaustive]
pub enum ReplayError {
    DuplicateRuleId(Uuid),
    /// A rule was due more than [`MAX_STEPS_PER_GAP`] times in one gap.
    StepLimit {
        rule_id: Uuid,
        at: DateTime<Utc>,
    },
}

impl std::fmt::Display for ReplayError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ReplayError::DuplicateRuleId(id) => write!(f, "rule id {id} appears more than once"),
            ReplayError::StepLimit { rule_id, at } => write!(
                f,
                "rule {rule_id} changes more than {MAX_STEPS_PER_GAP} times between two ticks, at {}",
                format_instant(*at)
            ),
        }
    }
}

impl std::error::Error for ReplayError {}

/// Replays `rules` over `ticks`, which are in time order.
///
/// # Errors
/// Two rules share an id, or a rule reaches [`MAX_STEPS_PER_GAP`].
pub fn replay(
    rules: &[Rule],
    ticks: impl IntoIterator<Item = ReplayTick>,
    options: ReplayOptions,
) -> Result<ReplayOutcome, ReplayError> {
    let mut run = Run {
        rules: evaluation_order(rules)?
            .into_iter()
            .filter_map(|i| rules.get(i))
            .map(Prepared::new)
            .collect(),
        state: EngineState::new(),
        ctx: SensorContext::default(),
        suppressed: HashSet::new(),
        events: Vec::new(),
        ticks: options.include_ticks.then(Vec::new),
    };

    for tick in ticks {
        run.step_until(tick.at)?;
        let mut ctx = tick.context;
        ctx.active_alerts = std::mem::take(&mut run.ctx.active_alerts);
        if ctx.last_reading_at.is_none() && ctx.latest_timestamp.is_none() {
            ctx.last_reading_at = Some(tick.at);
        }
        run.ctx = ctx;
        run.suppressed = tick.suppressed_rule_ids;
        run.tick(tick.at, tick.reading);
    }
    if let Some(until) = options.until {
        run.step_until(until)?;
    }

    let leaf_transitions = run
        .rules
        .iter_mut()
        .filter(|p| !p.leaf_log.is_empty())
        .map(|p| RuleLeafLog {
            rule_id: p.rule.id,
            leaves: std::mem::take(&mut p.leaf_log)
                .into_iter()
                .map(|(_, points)| points)
                .collect(),
        })
        .collect();

    Ok(ReplayOutcome {
        order: run.rules.iter().map(|p| p.rule.id).collect(),
        events: run.events,
        leaf_transitions,
        ticks: run.ticks,
    })
}

/// A rule with its body read once for the call, and how a host schedules it.
struct Prepared<'a> {
    rule: &'a Rule,
    /// `None` when it cannot be evaluated: the rule is skipped.
    body: Option<Node>,
    /// The live sweep evaluates it between readings (engine-semantics.md §5.1).
    wall_clock: bool,
    /// It reads a fact other than glucose, the time zone, the sensor's state
    /// and the target range, which a write between readings moves. The
    /// sensor's state moves with a reading, or through device-status writes
    /// that evaluate nothing; the time zone and the target range move between
    /// readings with the clock, which evaluates nothing live.
    reads_other_facts: bool,
    reads_active_alerts: bool,
    /// When a host next has to evaluate it, or for a rule the sweep does not
    /// evaluate, close its elapsed hysteresis window.
    due: Option<DateTime<Utc>>,
    steps: usize,
    leaf_log: Vec<(Truth, Vec<LeafPoint>)>,
}

impl<'a> Prepared<'a> {
    fn new(rule: &'a Rule) -> Self {
        let conditions = RuleConditions::from(rule);
        let reads = rule_reads(&conditions);
        Prepared {
            rule,
            body: rule.parse_body().ok(),
            wall_clock: references_wall_clock(&conditions),
            reads_other_facts: reads.iter().any(|f| {
                !matches!(
                    f,
                    ContextFact::Glucose
                        | ContextFact::TimeZone
                        | ContextFact::SensorState
                        | ContextFact::TargetRange
                )
            }),
            reads_active_alerts: reads.contains(&ContextFact::ActiveAlerts),
            due: None,
            steps: 0,
            leaf_log: Vec::new(),
        }
    }
}

struct Run<'a> {
    rules: Vec<Prepared<'a>>,
    state: EngineState,
    /// The latest tick's context, holding the replay's active alerts.
    ctx: SensorContext,
    suppressed: HashSet<Uuid>,
    events: Vec<ReplayEvent>,
    ticks: Option<Vec<ReplayTickOutcome>>,
}

impl Run<'_> {
    /// A reading evaluates every rule. A sample is a sweep: it closes each
    /// elapsed hysteresis window, then evaluates the rules the live sweep
    /// evaluates and those reading a fact a write may have moved.
    fn tick(&mut self, at: DateTime<Utc>, reading: bool) {
        let mut outcomes = Vec::new();
        for i in 0..self.rules.len() {
            let Some(p) = self.rules.get_mut(i) else {
                continue;
            };
            p.steps = 0;
            let evaluated = p.wall_clock || p.reads_other_facts;
            if p.body.is_none() {
                outcomes.push(ReplayRuleTick {
                    rule_id: p.rule.id,
                    state: None,
                });
                continue;
            }
            if !reading {
                self.sweep(i, at);
            }
            if reading || evaluated {
                outcomes.extend(self.evaluate(i, at));
            }
        }
        self.record_tick(at, outcomes);
    }

    /// Steps every rule due before `limit`, each instant in turn, as the live
    /// sweep does: at an instant, a due rule has its elapsed hysteresis window
    /// closed, then a due rule the sweep evaluates is evaluated against the
    /// latest tick's context with only `now` advanced. A rule that reads the
    /// active alerts is stepped as well when a rule before it opened or closed
    /// there.
    fn step_until(&mut self, limit: DateTime<Utc>) -> Result<(), ReplayError> {
        while let Some(at) = self
            .rules
            .iter()
            .filter_map(|p| p.due)
            .filter(|&due| due < limit)
            .min()
        {
            let mut outcomes = Vec::new();
            let mut alerts_moved = false;
            for i in 0..self.rules.len() {
                let Some(p) = self.rules.get_mut(i) else {
                    continue;
                };
                let due = p.due.is_some_and(|due| due <= at);
                if !due && !(alerts_moved && p.reads_active_alerts && p.body.is_some()) {
                    continue;
                }
                p.steps = p.steps.saturating_add(1);
                if p.steps > MAX_STEPS_PER_GAP {
                    return Err(ReplayError::StepLimit {
                        rule_id: p.rule.id,
                        at,
                    });
                }
                let before = self.ctx.active_alerts.contains_key(&p.rule.id);
                let evaluated = p.wall_clock
                    || !due
                    || self
                        .state
                        .tracker
                        .state(p.rule.id)
                        .and_then(TrackerState::hold_bound_at)
                        .is_some_and(|bound| bound <= at);
                self.sweep(i, at);
                if evaluated {
                    outcomes.extend(self.evaluate(i, at));
                }
                alerts_moved |= before
                    != self
                        .rules
                        .get(i)
                        .is_some_and(|p| self.ctx.active_alerts.contains_key(&p.rule.id));
            }
            self.record_tick(at, outcomes);
        }
        Ok(())
    }

    /// One evaluation through the live driver sequence (engine-semantics.md
    /// §7) and its events.
    fn evaluate(&mut self, i: usize, at: DateTime<Utc>) -> Option<ReplayRuleTick> {
        let p = self.rules.get_mut(i)?;
        let body = p.body.as_ref()?;
        let rule = p.rule;
        let outcome = evaluate_parsed_rule(rule, body, &self.ctx, at, &mut self.state, true);
        let e = outcome.evaluation?;
        record_leaves(&mut p.leaf_log, e.leaves.clone().unwrap_or_default(), at);
        p.due = if p.wall_clock {
            e.next_change_at
        } else {
            sweep_due(e.tracker.as_ref(), e.next_change_at, at)
        };
        let firing = self.state.tracker.active_excursion_id(rule.id).is_some();
        self.record_evaluation(rule.id, &e, at);
        Some(ReplayRuleTick {
            rule_id: rule.id,
            state: Some(ReplayRuleState {
                met: e.root,
                firing,
            }),
        })
    }

    /// What the live sweep does to a rule it does not evaluate: close its
    /// hysteresis window once elapsed (engine-semantics.md §6.1).
    fn sweep(&mut self, i: usize, at: DateTime<Utc>) {
        let Some(p) = self.rules.get_mut(i) else {
            return;
        };
        let rule = p.rule;
        let config = TrackerRuleConfig {
            hysteresis_minutes: rule.hysteresis_minutes,
        };
        let transition = self
            .state
            .tracker
            .close_elapsed_hysteresis(rule.id, config, at);
        let tracker = self.state.tracker.state(rule.id);
        p.due = match (&p.body, tracker) {
            (Some(body), Some(s)) => sweep_due(
                Some(s),
                (s.state == TrackerStateKind::Hysteresis)
                    .then(|| next_change_at(rule, body, &self.ctx, at, &self.state, true))
                    .flatten(),
                at,
            ),
            _ => None,
        };
        if transition.kind == TransitionType::ExcursionClosed {
            self.ctx.active_alerts.remove(&rule.id);
            self.push(at, rule.id, ReplayEventKind::Closed);
        }
    }

    fn record_evaluation(&mut self, rule_id: Uuid, e: &Evaluation, at: DateTime<Utc>) {
        match e.transition.kind {
            TransitionType::ExcursionOpened => {
                let kind = if self.suppressed.contains(&rule_id) {
                    ReplayEventKind::SuppressedByDnd
                } else {
                    ReplayEventKind::Fired
                };
                self.ctx.active_alerts.insert(
                    rule_id,
                    ActiveAlertSnapshot {
                        state: "firing".to_owned(),
                        triggered_at: at,
                        acknowledged_at: None,
                    },
                );
                self.push(at, rule_id, kind);
            }
            TransitionType::HysteresisStarted => {
                self.push(at, rule_id, ReplayEventKind::HysteresisStarted);
            }
            TransitionType::HysteresisResumed => {
                self.push(at, rule_id, ReplayEventKind::HysteresisResumed);
            }
            TransitionType::ExcursionClosed => {
                self.ctx.active_alerts.remove(&rule_id);
                let kind = match e.transition.close_reason {
                    Some(CloseReason::AutoResolve) => ReplayEventKind::AutoResolved,
                    _ => ReplayEventKind::Closed,
                };
                self.push(at, rule_id, kind);
            }
            _ => {}
        }
        if e.auto_resolved {
            self.ctx.active_alerts.remove(&rule_id);
            self.push(at, rule_id, ReplayEventKind::AutoResolved);
        }
    }

    fn push(&mut self, at: DateTime<Utc>, rule_id: Uuid, kind: ReplayEventKind) {
        self.events.push(ReplayEvent { at, rule_id, kind });
    }

    fn record_tick(&mut self, at: DateTime<Utc>, rules: Vec<ReplayRuleTick>) {
        if let Some(ticks) = &mut self.ticks
            && !rules.is_empty()
        {
            ticks.push(ReplayTickOutcome { at, rules });
        }
    }
}

/// When the sweep next acts on a rule it does not evaluate for the clock: in
/// hysteresis at `next_change_at`, which reaches the window's end no later
/// than it falls, and held where the hold reaches its bound, to evaluate it
/// there (engine-semantics.md §6.4).
fn sweep_due(
    t: Option<&TrackerState>,
    next_change_at: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
) -> Option<DateTime<Utc>> {
    let t = t?;
    let window = next_change_at.filter(|_| t.state == TrackerStateKind::Hysteresis);
    let bound = t.hold_bound_at().map(|at| {
        now.checked_add_signed(crate::next_change::RESOLUTION)
            .map_or(at, |next| at.max(next))
    });
    window.into_iter().chain(bound).min()
}

/// Each entry holds the leaf's last value and its points; a leaf turning
/// unknown, or known again, is a flip.
fn record_leaves(log: &mut Vec<(Truth, Vec<LeafPoint>)>, values: Vec<Truth>, at: DateTime<Utc>) {
    for (leaf_id, value) in values.into_iter().enumerate() {
        match log.get_mut(leaf_id) {
            Some((last, points)) => {
                if *last != value {
                    *last = value;
                    points.push(LeafPoint { at, value });
                }
            }
            None => log.push((value, vec![LeafPoint { at, value }])),
        }
    }
}

/// Indices into `rules`, each rule after every rule it references through
/// `alert_state`; a cycle keeps the given order.
fn evaluation_order(rules: &[Rule]) -> Result<Vec<usize>, ReplayError> {
    let mut index = HashMap::with_capacity(rules.len());
    for (i, rule) in rules.iter().enumerate() {
        if index.insert(rule.id, i).is_some() {
            return Err(ReplayError::DuplicateRuleId(rule.id));
        }
    }
    let dependencies: Vec<Vec<usize>> = rules
        .iter()
        .map(|rule| {
            let mut deps = Vec::new();
            for id in alert_state_references(rule) {
                if let Some(&dep) = index.get(&id)
                    && !deps.contains(&dep)
                {
                    deps.push(dep);
                }
            }
            deps
        })
        .collect();

    let mut visited = vec![false; rules.len()];
    let mut order = Vec::with_capacity(rules.len());
    for i in 0..rules.len() {
        let mut path = HashSet::new();
        if !visit(i, &dependencies, &mut visited, &mut path, &mut order) {
            return Ok((0..rules.len()).collect());
        }
    }
    Ok(order)
}

/// Depth-first, dependencies first; false on a cycle.
fn visit(
    i: usize,
    dependencies: &[Vec<usize>],
    visited: &mut [bool],
    path: &mut HashSet<usize>,
    order: &mut Vec<usize>,
) -> bool {
    if visited.get(i).copied().unwrap_or(true) {
        return true;
    }
    if !path.insert(i) {
        return false;
    }
    for &dep in dependencies.get(i).into_iter().flatten() {
        if !visit(dep, dependencies, visited, path, order) {
            return false;
        }
    }
    path.remove(&i);
    if let Some(v) = visited.get_mut(i) {
        *v = true;
    }
    order.push(i);
    true
}

/// The `alert_state` targets in the rule body, in pre-order; a body that
/// cannot be evaluated references nothing.
fn alert_state_references(rule: &Rule) -> Vec<Uuid> {
    let Ok(body) = rule.parse_body() else {
        return Vec::new();
    };
    collect_leaves(&body)
        .into_iter()
        .filter_map(|leaf| match leaf.payload() {
            Payload::AlertState(p) => Some(p.alert_id),
            _ => None,
        })
        .collect()
}

impl ReplayOutcome {
    /// The replay corpus result shape.
    #[must_use]
    pub fn to_json(&self) -> Value {
        let mut o = Map::new();
        o.insert(
            "order".into(),
            self.order.iter().map(|id| id.to_string()).collect(),
        );
        o.insert(
            "events".into(),
            self.events
                .iter()
                .map(|e| {
                    json!({
                        "at": format_instant(e.at),
                        "rule_id": e.rule_id.to_string(),
                        "kind": e.kind.wire(),
                    })
                })
                .collect(),
        );
        o.insert(
            "leaf_transitions".into(),
            self.leaf_transitions.iter().map(leaf_log_json).collect(),
        );
        if let Some(ticks) = &self.ticks {
            o.insert("ticks".into(), ticks.iter().map(tick_json).collect());
        }
        Value::Object(o)
    }
}

fn leaf_log_json(log: &RuleLeafLog) -> Value {
    let leaves = log.leaves.iter().enumerate().map(|(leaf_id, points)| {
        json!({
            "leaf_id": leaf_id,
            "points": points
                .iter()
                .map(|p| {
                    let mut point = Map::new();
                    point.insert("at_ms".into(), p.at.timestamp_millis().into());
                    p.value.write(&mut point, "value");
                    Value::Object(point)
                })
                .collect::<Vec<_>>(),
        })
    });
    json!({ "rule_id": log.rule_id.to_string(), "leaves": leaves.collect::<Vec<_>>() })
}

fn tick_json(tick: &ReplayTickOutcome) -> Value {
    let rules = tick.rules.iter().map(|r| match r.state {
        None => json!({ "rule_id": r.rule_id.to_string(), "skipped": true }),
        Some(s) => {
            let mut o = Map::new();
            o.insert("rule_id".into(), r.rule_id.to_string().into());
            s.met.write(&mut o, "met");
            o.insert("firing".into(), s.firing.into());
            Value::Object(o)
        }
    });
    json!({ "at": format_instant(tick.at), "rules": rules.collect::<Vec<_>>() })
}
