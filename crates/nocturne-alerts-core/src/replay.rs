//! The replay driver (engine-semantics.md §8): a rule set re-evaluated over a
//! series of pre-enriched ticks with one fresh timer store and replay-local
//! firing state instead of the excursion tracker.

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use crate::context::{ActiveAlertSnapshot, SensorContext};
use crate::engine::{Rule, auto_resolve_holds, evaluate_body, format_instant};
use crate::model::{ConditionKind, Node, Payload, parse_payload_structure};
use crate::sustained::TimerStore;

/// One replay tick: the instant and the context enriched as of it.
#[derive(Debug, Clone, Deserialize)]
pub struct ReplayTick {
    pub at: DateTime<Utc>,
    /// Its `active_alerts` are replaced by the replay's own.
    pub context: SensorContext,
    /// The rules a fire opening on this tick is recorded as suppressed for by
    /// Do Not Disturb, which the host resolves.
    #[serde(default)]
    pub suppressed_rule_ids: HashSet<Uuid>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum ReplayEventKind {
    Fired,
    SuppressedByDnd,
    AutoResolved,
    /// The body went false while firing; replay closes without an event a
    /// host shows.
    Cleared,
}

impl ReplayEventKind {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            ReplayEventKind::Fired => "fired",
            ReplayEventKind::SuppressedByDnd => "suppressed_by_dnd",
            ReplayEventKind::AutoResolved => "auto_resolved",
            ReplayEventKind::Cleared => "cleared",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReplayEvent {
    pub at: DateTime<Utc>,
    pub rule_id: Uuid,
    pub kind: ReplayEventKind,
}

/// A rule's state after one tick.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ReplayRuleState {
    pub met: bool,
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
    /// In evaluation order.
    pub rules: Vec<ReplayRuleTick>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LeafPoint {
    pub at: DateTime<Utc>,
    pub value: bool,
}

/// A rule's leaf log: per leaf id, the first observation then every flip.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuleLeafLog {
    pub rule_id: Uuid,
    pub leaves: Vec<Vec<LeafPoint>>,
}

#[derive(Debug, Clone, Copy, Default)]
pub struct ReplayOptions {
    /// Report every rule's state on every tick.
    pub include_ticks: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReplayOutcome {
    /// Rule ids in evaluation order.
    pub order: Vec<Uuid>,
    /// In the order they happened: by tick, then evaluation order.
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
}

impl std::fmt::Display for ReplayError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ReplayError::DuplicateRuleId(id) => write!(f, "rule id {id} appears more than once"),
        }
    }
}

impl std::error::Error for ReplayError {}

/// Replays `rules` over `ticks` in order.
///
/// # Errors
/// Two rules share an id.
pub fn replay(
    rules: &[Rule],
    ticks: impl IntoIterator<Item = ReplayTick>,
    options: ReplayOptions,
) -> Result<ReplayOutcome, ReplayError> {
    let ordered: Vec<&Rule> = evaluation_order(rules)?
        .into_iter()
        .filter_map(|i| rules.get(i))
        .collect();

    let mut timers = TimerStore::new();
    let mut active = HashMap::new();
    let mut firing = vec![false; ordered.len()];
    let mut leaf_logs: Vec<Vec<(bool, Vec<LeafPoint>)>> = vec![Vec::new(); ordered.len()];
    let mut events = Vec::new();
    let mut tick_outcomes = options.include_ticks.then(Vec::new);

    for tick in ticks {
        let at = tick.at;
        let mut ctx = tick.context;
        ctx.active_alerts = std::mem::take(&mut active);
        if ctx.last_reading_at.is_none() && ctx.latest_timestamp.is_none() {
            ctx.last_reading_at = Some(at);
        }

        let mut rule_ticks = Vec::with_capacity(ordered.len());
        for ((rule, was_firing), leaf_log) in ordered.iter().zip(&mut firing).zip(&mut leaf_logs) {
            let Some(body) = evaluate_body(rule, &ctx, at, &mut timers, true) else {
                rule_ticks.push(ReplayRuleTick {
                    rule_id: rule.id,
                    state: None,
                });
                continue;
            };
            record_leaves(leaf_log, body.leaves.unwrap_or_default(), at);

            let met = body.root;
            let mut now_firing = met;
            if met && !*was_firing {
                let kind = if tick.suppressed_rule_ids.contains(&rule.id) {
                    ReplayEventKind::SuppressedByDnd
                } else {
                    ReplayEventKind::Fired
                };
                events.push(event(at, rule.id, kind));
                ctx.active_alerts.insert(
                    rule.id,
                    ActiveAlertSnapshot {
                        state: "firing".to_owned(),
                        triggered_at: at,
                        acknowledged_at: None,
                    },
                );
            } else if !met && *was_firing {
                close(&mut ctx, &mut timers, rule.id);
                events.push(event(at, rule.id, ReplayEventKind::Cleared));
            }

            if now_firing
                && rule.auto_resolve_enabled
                && auto_resolve_holds(rule, &ctx, at, &mut timers)
            {
                close(&mut ctx, &mut timers, rule.id);
                events.push(event(at, rule.id, ReplayEventKind::AutoResolved));
                now_firing = false;
            }

            *was_firing = now_firing;
            timers.drain_ops();
            rule_ticks.push(ReplayRuleTick {
                rule_id: rule.id,
                state: Some(ReplayRuleState {
                    met,
                    firing: now_firing,
                }),
            });
        }
        active = ctx.active_alerts;
        if let Some(outcomes) = &mut tick_outcomes {
            outcomes.push(ReplayTickOutcome {
                at,
                rules: rule_ticks,
            });
        }
    }

    let leaf_transitions = ordered
        .iter()
        .zip(leaf_logs)
        .filter(|(_, log)| !log.is_empty())
        .map(|(rule, log)| RuleLeafLog {
            rule_id: rule.id,
            leaves: log.into_iter().map(|(_, points)| points).collect(),
        })
        .collect();

    Ok(ReplayOutcome {
        order: ordered.iter().map(|r| r.id).collect(),
        events,
        leaf_transitions,
        ticks: tick_outcomes,
    })
}

fn event(at: DateTime<Utc>, rule_id: Uuid, kind: ReplayEventKind) -> ReplayEvent {
    ReplayEvent { at, rule_id, kind }
}

/// A rule stops firing: it leaves the active alerts and its timers reset.
fn close(ctx: &mut SensorContext, timers: &mut TimerStore, rule_id: Uuid) {
    ctx.active_alerts.remove(&rule_id);
    timers.clear_all_for_rule(rule_id);
}

/// Each entry holds the leaf's last value and its points.
fn record_leaves(log: &mut Vec<(bool, Vec<LeafPoint>)>, values: Vec<bool>, at: DateTime<Utc>) {
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

/// The `alert_state` targets in the rule body, in pre-order. Every payload
/// property a node carries is walked, whatever its `type`; a body that does
/// not parse references nothing.
fn alert_state_references(rule: &Rule) -> Vec<Uuid> {
    let mut found = Vec::new();
    if !rule.condition_params.is_null()
        && let Ok(payload) = parse_payload_structure(rule.condition_type, &rule.condition_params)
    {
        collect_references(
            &Node::from_rule(rule.condition_type, Some(payload)),
            &mut found,
        );
    }
    found
}

fn collect_references(node: &Node, found: &mut Vec<Uuid>) {
    if let Some(Payload::AlertState(p)) = node.payload(ConditionKind::AlertState) {
        found.push(p.alert_id);
    }
    if let Some(Payload::Composite(p)) = node.payload(ConditionKind::Composite) {
        for child in p.conditions.iter().flatten().flatten() {
            collect_references(child, found);
        }
    }
    if let Some(Payload::Not(p)) = node.payload(ConditionKind::Not)
        && let Some(child) = &p.child
    {
        collect_references(child, found);
    }
    if let Some(Payload::Sustained(p)) = node.payload(ConditionKind::Sustained)
        && let Some(child) = &p.child
    {
        collect_references(child, found);
    }
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
                .map(|p| json!({ "at_ms": p.at.timestamp_millis(), "value": p.value }))
                .collect::<Vec<_>>(),
        })
    });
    json!({ "rule_id": log.rule_id.to_string(), "leaves": leaves.collect::<Vec<_>>() })
}

fn tick_json(tick: &ReplayTickOutcome) -> Value {
    let rules = tick.rules.iter().map(|r| match r.state {
        None => json!({ "rule_id": r.rule_id.to_string(), "skipped": true }),
        Some(s) => json!({ "rule_id": r.rule_id.to_string(), "met": s.met, "firing": s.firing }),
    });
    json!({ "at": format_instant(tick.at), "rules": rules.collect::<Vec<_>>() })
}
