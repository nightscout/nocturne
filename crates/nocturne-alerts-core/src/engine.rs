//! The per-rule, per-tick driver (engine-semantics.md §7): root evaluation,
//! the leaf log, the excursion tracker, then auto-resolve.

use chrono::{DateTime, SecondsFormat, Utc};
use serde_json::{Map, Value, json};
use uuid::Uuid;

use crate::context::SensorContext;
use crate::eval::{Env, eval_node, eval_payload};
use crate::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerState, Transition, TransitionType,
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
    /// The payload object as stored in `condition_params`.
    pub condition_params: Value,
    pub confirmation_readings: i32,
    pub hysteresis_minutes: i32,
    pub auto_resolve_enabled: bool,
    /// A full condition node, or `None`.
    pub auto_resolve_params: Option<Value>,
}

/// Evaluation state carried across ticks: sustained timers and the excursion
/// tracker.
#[derive(Debug, Default)]
pub struct EngineState {
    pub timers: TimerStore,
    pub tracker: ExcursionTracker,
}

impl EngineState {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
}

/// Everything observable from one rule evaluation on one tick.
#[derive(Debug, Clone)]
pub struct RuleOutcome {
    pub rule_id: Uuid,
    /// `None` when the rule body cannot be evaluated (engine-semantics.md
    /// §1.4): nothing was evaluated and no state changed.
    pub evaluation: Option<Evaluation>,
}

#[derive(Debug, Clone)]
pub struct Evaluation {
    pub root: bool,
    /// Each leaf evaluated alone, indexed by leaf id; empty unless requested.
    pub leaves: Vec<bool>,
    pub transition: Transition,
    pub tracker: Option<TrackerState>,
    pub auto_resolved: bool,
    /// Timer mutations from the root evaluation then auto-resolve, in order.
    pub timer_ops: Vec<TimerOp>,
}

/// An instant as RFC 3339 UTC: whole seconds without a fraction, sub-second
/// instants with their precision.
#[must_use]
pub fn format_instant(at: DateTime<Utc>) -> String {
    at.to_rfc3339_opts(SecondsFormat::AutoSi, true)
}

impl TimerOp {
    #[must_use]
    pub fn to_json(&self) -> Value {
        let mut o = Map::new();
        o.insert("op".into(), self.kind.wire().into());
        o.insert("path".into(), self.path.clone().into());
        if let Some(at) = self.at {
            o.insert("at".into(), format_instant(at).into());
        }
        Value::Object(o)
    }
}

impl RuleOutcome {
    /// The corpus result shape (`ExpectedRuleResult`).
    #[must_use]
    pub fn to_json(&self) -> Value {
        let mut o = Map::new();
        o.insert("rule_id".into(), self.rule_id.to_string().into());
        let Some(e) = &self.evaluation else {
            o.insert("skipped".into(), true.into());
            return Value::Object(o);
        };
        o.insert("root".into(), e.root.into());
        let leaves = e.leaves.iter().enumerate();
        o.insert(
            "leaves".into(),
            leaves
                .map(|(leaf_id, value)| json!({ "leaf_id": leaf_id, "value": value }))
                .collect(),
        );
        o.insert("transition".into(), e.transition.kind.wire().into());
        if let Some(reason) = e.transition.close_reason {
            o.insert("close_reason".into(), reason.wire().into());
        }
        if let Some(tracker) = &e.tracker {
            let mut t = Map::new();
            t.insert("state".into(), tracker.state.wire().into());
            t.insert(
                "confirmation_count".into(),
                tracker.confirmation_count.into(),
            );
            if let Some(excursion) = tracker.active_excursion {
                t.insert("excursion".into(), excursion.into());
            }
            if let Some(at) = tracker.hysteresis_started_at {
                t.insert("hysteresis_started_at".into(), format_instant(at).into());
            }
            o.insert("tracker".into(), Value::Object(t));
        }
        if e.auto_resolved {
            o.insert("auto_resolved".into(), true.into());
        }
        if !e.timer_ops.is_empty() {
            o.insert(
                "timer_ops".into(),
                e.timer_ops.iter().map(TimerOp::to_json).collect(),
            );
        }
        Value::Object(o)
    }
}

/// Evaluates every rule, in order, for one tick, logging every leaf.
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

/// Evaluates one rule for one tick.
pub fn evaluate_rule(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> RuleOutcome {
    // A JSON null body is a null condition record, which evaluates false.
    let payload = match &rule.condition_params {
        Value::Null => None,
        v => match parse_payload(rule.condition_type, v) {
            Ok(p) => Some(p),
            Err(_) => {
                return RuleOutcome {
                    rule_id: rule.id,
                    evaluation: None,
                };
            }
        },
    };

    let wire = rule.condition_type.wire();
    let mut env = Env::new(now, rule.id, ctx, &mut state.timers);
    let root = payload
        .as_ref()
        .is_some_and(|p| eval_payload(p, wire, &mut env));

    // Leaves evaluate alone, with no short-circuit, at the rule's root path;
    // a leaf touches no timers.
    let full_node = Node::from_rule(rule.condition_type, payload);
    let leaves = collect_leaves(&full_node)
        .into_iter()
        .map(|leaf| eval_node(leaf, wire, &mut env))
        .collect();

    let config = TrackerRuleConfig {
        confirmation_readings: rule.confirmation_readings,
        hysteresis_minutes: rule.hysteresis_minutes,
    };
    let transition = state.tracker.process_evaluation(rule.id, config, root, now);
    let auto_resolved = rule.auto_resolve_enabled && try_auto_resolve(rule, ctx, now, state);

    RuleOutcome {
        rule_id: rule.id,
        evaluation: Some(Evaluation {
            root,
            leaves,
            transition,
            tracker: state.tracker.state(rule.id).copied(),
            auto_resolved,
            timer_ops: state.timers.drain_ops(),
        }),
    }
}

/// Only while an excursion is active or in hysteresis. A tree that does not
/// parse never resolves; one that evaluates true at the `auto_resolve` root
/// force-closes the excursion.
fn try_auto_resolve(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> bool {
    if state.tracker.active_excursion_id(rule.id).is_none() {
        return false;
    }
    let Some(node) = rule
        .auto_resolve_params
        .as_ref()
        .filter(|v| !v.is_null())
        .and_then(|v| Node::parse(v).ok())
    else {
        return false;
    };
    let mut env = Env::new(now, rule.id, ctx, &mut state.timers);
    if !eval_node(Some(&node), AUTO_RESOLVE_ROOT, &mut env) {
        return false;
    }
    let transition = state
        .tracker
        .force_close(rule.id, CloseReason::AutoResolve, now);
    transition.kind == TransitionType::ExcursionClosed
}
