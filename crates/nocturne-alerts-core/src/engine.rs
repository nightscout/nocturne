//! Per-rule / per-tick driver used by the parity harness. Mirrors
//! `AlertOrchestrator.EvaluateRuleAsync` (root eval with the canonical
//! wire-string root path → excursion tracker → unconditional auto-resolve
//! under the `auto_resolve` path root) plus the replay path's force-eval of
//! every leaf for the leaf log.

use chrono::{DateTime, Utc};
use serde_json::Value;
use uuid::Uuid;

use crate::context::SensorContext;
use crate::eval::{Env, eval_node, eval_payload};
use crate::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerStateKind, Transition, TransitionType,
};
use crate::leaf_identity::collect_leaves;
use crate::model::{ConditionKind, Node, parse_payload};
use crate::paths::AUTO_RESOLVE_ROOT;
use crate::sustained::{TimerOp, TimerStore};

/// An alert rule as stored: `(condition_type, condition_params)` plus tracker
/// and auto-resolve configuration.
#[derive(Debug, Clone)]
pub struct Rule {
    pub id: Uuid,
    pub condition_type: ConditionKind,
    /// The payload object exactly as stored in `alert_rules.condition_params`.
    pub condition_params: Value,
    pub confirmation_readings: i32,
    pub hysteresis_minutes: i32,
    pub auto_resolve_enabled: bool,
    /// A full ConditionNode object (`{"type": …, …}`), or `None`.
    pub auto_resolve_params: Option<Value>,
}

/// Mutable evaluation state persisted across ticks: sustained timers and the
/// excursion tracker.
#[derive(Debug, Default)]
pub struct EngineState {
    pub timers: TimerStore,
    pub tracker: ExcursionTracker,
}

impl EngineState {
    pub fn new() -> Self {
        Self::default()
    }
}

/// Snapshot of the tracker state after a rule evaluation.
#[derive(Debug, Clone, Copy)]
pub struct TrackerSnapshot {
    pub state: TrackerStateKind,
    pub confirmation_count: i32,
    /// 1-based ordinal of the active excursion, when one is active.
    pub excursion: Option<u32>,
    pub hysteresis_started_at: Option<DateTime<Utc>>,
}

/// Everything observable from one rule evaluation on one tick.
#[derive(Debug, Clone)]
pub struct RuleOutcome {
    pub rule_id: Uuid,
    /// The rule body failed to parse (engine-semantics.md §1.4): nothing was
    /// evaluated, no state changed, and every other field is empty.
    pub skipped: bool,
    pub root: Option<bool>,
    /// Per-leaf force-eval truths, ascending by leaf id.
    pub leaves: Vec<(i32, bool)>,
    pub transition: Option<Transition>,
    pub tracker: Option<TrackerSnapshot>,
    pub auto_resolved: bool,
    /// Timer mutations from the root eval then the auto-resolve eval, in
    /// execution order.
    pub timer_ops: Vec<TimerOp>,
}

impl RuleOutcome {
    fn skipped(rule_id: Uuid) -> Self {
        Self {
            rule_id,
            skipped: true,
            root: None,
            leaves: Vec::new(),
            transition: None,
            tracker: None,
            auto_resolved: false,
            timer_ops: Vec::new(),
        }
    }
}

/// Evaluates every rule (in order) against one tick.
pub fn evaluate_tick(
    rules: &[Rule],
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> Vec<RuleOutcome> {
    rules
        .iter()
        .map(|rule| evaluate_rule(rule, ctx, now, state))
        .collect()
}

/// Evaluates a single rule for one tick, mirroring the orchestrator contract.
pub fn evaluate_rule(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> RuleOutcome {
    let wire = rule.condition_type.wire();

    // A JSON null column is a null condition record, which evaluates false.
    let payload = match &rule.condition_params {
        Value::Null => None,
        v => match parse_payload(rule.condition_type, v) {
            Ok(p) => Some(p),
            Err(_) => return RuleOutcome::skipped(rule.id),
        },
    };

    let root = {
        let mut env = Env {
            now,
            rule_id: rule.id,
            ctx,
            timers: &mut state.timers,
        };
        match &payload {
            Some(p) => eval_payload(p, wire, &mut env),
            None => false,
        }
    };

    // Replay-parity leaf log: force-evaluate every leaf in isolation (no
    // short-circuit) with the rule-root context path. Leaves are stateless so
    // this contributes no timer ops.
    let full_node = Node::from_rule(rule.condition_type, payload);
    let leaves = {
        let mut env = Env {
            now,
            rule_id: rule.id,
            ctx,
            timers: &mut state.timers,
        };
        collect_leaves(&full_node)
            .into_iter()
            .enumerate()
            .map(|(leaf_id, leaf)| (leaf_id as i32, eval_node(leaf, wire, &mut env)))
            .collect()
    };

    let config = TrackerRuleConfig {
        confirmation_readings: rule.confirmation_readings,
        hysteresis_minutes: rule.hysteresis_minutes,
    };
    let transition = state.tracker.process_evaluation(rule.id, config, root, now);

    let mut auto_resolved = false;
    if rule.auto_resolve_enabled && rule.auto_resolve_params.is_some() {
        auto_resolved = try_auto_resolve(rule, ctx, now, state);
    }

    let tracker = state.tracker.state(rule.id).map(|s| TrackerSnapshot {
        state: s.state,
        confirmation_count: s.confirmation_count,
        excursion: s.active_excursion,
        hysteresis_started_at: s.hysteresis_started_at,
    });

    RuleOutcome {
        rule_id: rule.id,
        skipped: false,
        root: Some(root),
        leaves,
        transition: Some(transition),
        tracker,
        auto_resolved,
        timer_ops: state.timers.drain_ops(),
    }
}

/// Mirrors `AlertOrchestrator.TryAutoResolveAsync`: only while an excursion is
/// active (active/hysteresis); malformed JSON is skipped silently; the tree
/// evaluates with `CurrentPath = "auto_resolve"`; on true, force-close with
/// reason `auto`.
fn try_auto_resolve(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> bool {
    if state.tracker.active_excursion_id(rule.id).is_none() {
        return false;
    }

    let node = match rule.auto_resolve_params.as_ref() {
        // JSON null deserialises to a null node → false; a non-object throws
        // JsonException → skipped silently. Either way: no evaluation.
        Some(Value::Null) | None => return false,
        Some(v) => match Node::parse(v) {
            Ok(node) => node,
            Err(_) => return false,
        },
    };

    let should_resolve = {
        let mut env = Env {
            now,
            rule_id: rule.id,
            ctx,
            timers: &mut state.timers,
        };
        eval_node(Some(&node), AUTO_RESOLVE_ROOT, &mut env)
    };
    if !should_resolve {
        return false;
    }

    let transition = state
        .tracker
        .force_close(rule.id, CloseReason::AutoResolve, now);
    transition.kind == TransitionType::ExcursionClosed
}
