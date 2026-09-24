//! The per-rule, per-tick driver (engine-semantics.md §7): root evaluation,
//! the leaf log, the excursion tracker, then auto-resolve.

use chrono::{DateTime, SecondsFormat, Utc};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use crate::context::SensorContext;
use crate::enums::WireEnum;
use crate::eval::{Env, eval_node, eval_payload};
use crate::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerState, Transition, TransitionType,
};
use crate::leaf_identity::collect_leaves;
use crate::model::{ConditionKind, Node, ParseResult, parse_payload};
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

impl Rule {
    /// The body as the full node `{"type": <wire>, "<wire>": <payload>}`,
    /// parsed and checked for evaluability (engine-semantics.md §1.4). A JSON
    /// `null` body has no payload property, and its root evaluates false.
    ///
    /// # Errors
    /// The body cannot be evaluated.
    pub fn parse_body(&self) -> ParseResult<Node> {
        let payload = match &self.condition_params {
            Value::Null => None,
            v => Some(parse_payload(self.condition_type, v)?),
        };
        Ok(Node::from_rule(self.condition_type, payload))
    }

    /// The auto-resolve tree when it is present, not `null` and evaluable.
    /// Whether auto-resolve is enabled is for the caller to check.
    pub(crate) fn parse_auto_resolve(&self) -> Option<Node> {
        self.auto_resolve_params
            .as_ref()
            .filter(|v| !v.is_null())
            .and_then(|v| Node::parse(v).ok())
    }
}

/// A rule in the golden corpus `ScenarioRule` shape, which the FFI envelopes
/// also take; unknown fields such as `name` are ignored.
#[derive(Debug, Clone, Deserialize)]
pub struct WireRule {
    pub id: Uuid,
    /// A kind's wire name, ignoring ASCII case.
    pub condition_type: String,
    #[serde(default)]
    pub condition_params: Value,
    #[serde(default = "one")]
    pub confirmation_readings: i32,
    #[serde(default)]
    pub hysteresis_minutes: i32,
    #[serde(default)]
    pub auto_resolve_enabled: bool,
    #[serde(default)]
    pub auto_resolve_params: Option<Value>,
}

fn one() -> i32 {
    1
}

/// Only the `condition_type` is checked, not the body.
impl TryFrom<WireRule> for Rule {
    type Error = String;

    fn try_from(w: WireRule) -> Result<Self, String> {
        let condition_type = ConditionKind::from_name(&w.condition_type).ok_or_else(|| {
            format!(
                "unknown condition_type '{}'",
                w.condition_type.escape_default()
            )
        })?;
        Ok(Rule {
            id: w.id,
            condition_type,
            condition_params: w.condition_params,
            confirmation_readings: w.confirmation_readings,
            hysteresis_minutes: w.hysteresis_minutes,
            auto_resolve_enabled: w.auto_resolve_enabled,
            auto_resolve_params: w.auto_resolve_params,
        })
    }
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
    /// Each leaf evaluated alone, indexed by leaf id, when requested.
    pub leaves: Option<Vec<bool>>,
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
        if let Some(leaves) = &e.leaves {
            let leaves = leaves.iter().enumerate();
            o.insert(
                "leaves".into(),
                leaves
                    .map(|(leaf_id, value)| json!({ "leaf_id": leaf_id, "value": value }))
                    .collect(),
            );
        }
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
            if tracker.awaiting_rearm {
                t.insert("awaiting_rearm".into(), true.into());
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
        .map(|rule| evaluate_rule(rule, ctx, now, state, true))
        .collect()
}

/// Evaluates one rule for one tick, and each leaf alone when `log_leaves`.
pub fn evaluate_rule(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
    log_leaves: bool,
) -> RuleOutcome {
    match rule.parse_body() {
        Ok(body) => evaluate_parsed_rule(rule, &body, ctx, now, state, log_leaves),
        Err(_) => RuleOutcome {
            rule_id: rule.id,
            evaluation: None,
        },
    }
}

/// [`evaluate_rule`] with the body already read by [`Rule::parse_body`].
pub fn evaluate_parsed_rule(
    rule: &Rule,
    body: &Node,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
    log_leaves: bool,
) -> RuleOutcome {
    let body = evaluate_body(rule, body, ctx, now, &mut state.timers, log_leaves);

    let config = TrackerRuleConfig {
        confirmation_readings: rule.confirmation_readings,
        hysteresis_minutes: rule.hysteresis_minutes,
    };
    let transition = state
        .tracker
        .process_evaluation(rule.id, config, body.root, now);
    let auto_resolved = rule.auto_resolve_enabled && try_auto_resolve(rule, ctx, now, state);

    RuleOutcome {
        rule_id: rule.id,
        evaluation: Some(Evaluation {
            root: body.root,
            leaves: body.leaves,
            transition,
            tracker: state.tracker.state(rule.id).copied(),
            auto_resolved,
            timer_ops: state.timers.drain_ops(),
        }),
    }
}

/// A rule body's truth for one tick.
pub(crate) struct BodyOutcome {
    pub(crate) root: bool,
    pub(crate) leaves: Option<Vec<bool>>,
}

/// The root truth of a body read by [`Rule::parse_body`] and, when
/// `log_leaves`, each leaf alone.
pub(crate) fn evaluate_body(
    rule: &Rule,
    body: &Node,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    timers: &mut TimerStore,
    log_leaves: bool,
) -> BodyOutcome {
    let wire = rule.condition_type.name();
    let mut env = Env::new(now, rule.id, ctx, timers);
    let root = body
        .payload(rule.condition_type)
        .is_some_and(|p| eval_payload(p, wire, &mut env));

    // Leaves evaluate alone, with no short-circuit, at the rule's root path;
    // a leaf touches no timers.
    let leaves = log_leaves.then(|| {
        collect_leaves(body)
            .into_iter()
            .map(|leaf| eval_node(leaf, wire, &mut env))
            .collect()
    });
    BodyOutcome { root, leaves }
}

/// Only while an excursion is active or in hysteresis; one that the
/// auto-resolve tree holds for force-closes the excursion.
fn try_auto_resolve(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> bool {
    if state.tracker.active_excursion_id(rule.id).is_none()
        || !auto_resolve_holds(
            rule.id,
            rule.parse_auto_resolve().as_ref(),
            ctx,
            now,
            &mut state.timers,
        )
    {
        return false;
    }
    let transition = state
        .tracker
        .force_close(rule.id, CloseReason::AutoResolve, now);
    transition.kind == TransitionType::ExcursionClosed
}

/// Whether an auto-resolve tree read by [`Rule::parse_auto_resolve`]
/// evaluates true at the `auto_resolve` root; no tree never holds.
pub(crate) fn auto_resolve_holds(
    rule_id: Uuid,
    tree: Option<&Node>,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    timers: &mut TimerStore,
) -> bool {
    let Some(tree) = tree else {
        return false;
    };
    let mut env = Env::new(now, rule_id, ctx, timers);
    eval_node(Some(tree), AUTO_RESOLVE_ROOT, &mut env)
}
