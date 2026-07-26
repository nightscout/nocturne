//! Per-rule / per-tick driver used by the parity harness. Mirrors
//! `AlertOrchestrator.EvaluateRuleAsync` (root eval with the canonical
//! wire-string root path → excursion tracker → unconditional auto-resolve
//! under the `auto_resolve` path root) plus the replay path's force-eval of
//! every leaf for the leaf log.

use chrono::{DateTime, Utc};
use serde_json::{Value, json};
use uuid::Uuid;

use crate::context::SensorContext;
use crate::eval::{Env, eval_kind, eval_node};
use crate::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerStateKind, Transition, TransitionType,
};
use crate::leaf_identity::collect_leaves;
use crate::model::{ConditionKind, Node, parse_payload};
use crate::paths::{AUTO_RESOLVE_ROOT, SNOOZE_ROOT};
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
}

/// Everything observable from one rule evaluation on one tick.
#[derive(Debug, Clone)]
pub struct RuleOutcome {
    pub rule_id: Uuid,
    /// True when no evaluator exists for the root condition type
    /// (`signal_loss`): the rule is skipped entirely — no tracker call, no
    /// auto-resolve, and every other field is empty.
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

/// Evaluates a single rule for one tick, mirroring the orchestrator contract.
///
/// Callers drive their own per-tick loop over rules: a host that also runs
/// smart snooze has to interleave [`evaluate_snooze_conditions`] between this
/// call and its timer drain, so a batching wrapper here would only ever fit the
/// rule-body-only case.
pub fn evaluate_rule(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> RuleOutcome {
    // signal_loss has no registered evaluator: orchestrator parity is to skip
    // the rule entirely.
    if rule.condition_type == ConditionKind::SignalLoss {
        return RuleOutcome {
            rule_id: rule.id,
            skipped: true,
            root: None,
            leaves: Vec::new(),
            transition: None,
            tracker: None,
            auto_resolved: false,
            timer_ops: Vec::new(),
        };
    }

    let wire = rule.condition_type.wire();

    // Root eval: the evaluator receives the stored payload directly. A JSON
    // null column is a null condition record (false); a structurally
    // malformed payload would throw uncaught in C# (such rules cannot exist
    // in the corpus) — fail closed here.
    let payload = match &rule.condition_params {
        Value::Null => None,
        v => parse_payload(rule.condition_type, v).ok(),
    };

    let root = {
        let mut env = Env {
            now,
            rule_id: rule.id,
            ctx,
            timers: &mut state.timers,
        };
        match &payload {
            Some(p) => eval_kind(rule.condition_type, Some(p), wire, &mut env),
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

/// Mirrors `AlertSweepService.EvaluateSnoozeConditionsAsync`: a rule's
/// configured smart-snooze conditions are wrapped as
/// `composite{and, conditions}` and evaluated under the reserved `snooze` path
/// root, so a `sustained` inside them keys its timer separately from the rule
/// body's and auto-resolve's.
///
/// Deliberately not part of [`evaluate_rule`]: snooze evaluation belongs to the
/// sweep, driven by a snooze expiring rather than by a reading. Hosts compose
/// the two calls (the FFI exposes this scope as `evaluate_node` with
/// `root: "snooze"`); the corpus harness composes them per tick.
///
/// The host-side extend/clear policy around this predicate (max counts, extend
/// minutes, the trend-favourable fallback when no conditions are configured)
/// stays with the caller — see `docs/alerts/engine-semantics.md` §9.
pub fn evaluate_snooze_conditions(
    rule_id: Uuid,
    conditions: &[Value],
    ctx: &SensorContext,
    now: DateTime<Utc>,
    timers: &mut TimerStore,
) -> bool {
    let wrapped = json!({
        "type": "composite",
        "composite": { "operator": "and", "conditions": conditions },
    });
    let Ok(node) = Node::parse(&wrapped) else {
        return false;
    };

    let mut env = Env {
        now,
        rule_id,
        ctx,
        timers,
    };
    eval_node(Some(&node), SNOOZE_ROOT, &mut env)
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
