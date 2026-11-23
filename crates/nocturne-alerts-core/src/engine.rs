//! The per-rule, per-tick driver (engine-semantics.md §7): root evaluation,
//! the leaf log, the excursion tracker, then auto-resolve.

use chrono::{DateTime, SecondsFormat, TimeDelta, Utc};
use serde::Deserialize;
use serde_json::{Map, Value};
use uuid::Uuid;

use crate::clock_anomaly::ClockAnomalies;
use crate::context::{SensorContext, opt_ts};
use crate::enums::{WireEnum, wire_enum};
use crate::eval::{
    DEFAULT_GLUCOSE_FRESHNESS, Env, MAX_GLUCOSE_FRESHNESS_MINUTES, MetPoint, eval_node,
    eval_payload_unknown, met_point,
};
use crate::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, TrackerState, TrackerStateKind, Transition,
    TransitionType, hysteresis_window,
};
use crate::leaf_identity::collect_leaves_with_polarity;
use crate::model::{ConditionKind, Node, ParseResult, parse_payload};
use crate::next_change::next_change_at;
use crate::paths::AUTO_RESOLVE_ROOT;
use crate::sustained::{TimerOp, TimerStore};
use crate::truth::Truth;
use crate::unknown::UnknownFacts;

wire_enum! {
    /// What an unknown root means to the tracker (engine-semantics.md §6.4).
    pub enum OnUnknown {
        Hold = "hold",
        Met = "met",
        NotMet = "not_met",
    }
}

impl OnUnknown {
    /// The root as the tracker reads it; `None` holds.
    #[must_use]
    pub fn resolve(self, root: Truth) -> Option<bool> {
        root.known().or(match self {
            OnUnknown::Hold => None,
            OnUnknown::Met => Some(true),
            OnUnknown::NotMet => Some(false),
        })
    }
}

/// An alert rule as stored: `(condition_type, condition_params)` plus tracker
/// and auto-resolve configuration.
#[derive(Debug, Clone)]
pub struct Rule {
    pub id: Uuid,
    pub condition_type: ConditionKind,
    /// The payload object as stored in `condition_params`.
    pub condition_params: Value,
    pub hysteresis_minutes: i32,
    pub auto_resolve_enabled: bool,
    /// A full condition node, or `None`.
    pub auto_resolve_params: Option<Value>,
    pub on_unknown: OnUnknown,
    /// How old a reading may be before the leaves that judge its freshness
    /// read it unknown (engine-semantics.md §3).
    pub glucose_freshness: TimeDelta,
    /// When the rule was last enabled (engine-semantics.md §3).
    pub enabled_at: Option<DateTime<Utc>>,
}

impl Rule {
    /// The body as the node `{"type": <wire>, "<wire>": <payload>}`.
    ///
    /// # Errors
    /// The body cannot be evaluated (engine-semantics.md §1.4).
    pub fn parse_body(&self) -> ParseResult<Node> {
        parse_payload(self.condition_type, &self.condition_params).map(Node::from)
    }

    /// The auto-resolve tree when it is present, not `null` and evaluable.
    /// Whether auto-resolve is enabled is for the caller to check.
    pub(crate) fn parse_auto_resolve(&self) -> Option<Node> {
        parse_auto_resolve(self.auto_resolve_params.as_ref())
    }
}

/// An auto-resolve tree when it is present, not `null` and evaluable.
#[must_use]
pub fn parse_auto_resolve(params: Option<&Value>) -> Option<Node> {
    params
        .filter(|v| !v.is_null())
        .and_then(|v| Node::parse(v).ok())
}

/// A rule in the golden corpus `ScenarioRule` shape, which the FFI envelopes
/// also take; unknown fields such as `name` are ignored.
#[derive(Debug, Clone, Deserialize)]
pub struct WireRule {
    pub id: Uuid,
    /// Exactly a kind's wire name.
    pub condition_type: String,
    #[serde(default)]
    pub condition_params: Value,
    #[serde(default)]
    pub hysteresis_minutes: i32,
    #[serde(default)]
    pub auto_resolve_enabled: bool,
    #[serde(default)]
    pub auto_resolve_params: Option<Value>,
    /// Exactly a word of [`OnUnknown`]; absent is `hold`.
    #[serde(default)]
    pub on_unknown: Option<String>,
    /// A whole number of minutes up to [`MAX_GLUCOSE_FRESHNESS_MINUTES`];
    /// absent is [`DEFAULT_GLUCOSE_FRESHNESS`].
    #[serde(default)]
    pub glucose_freshness_minutes: Option<i64>,
    #[serde(default)]
    pub enabled_at: Option<DateTime<Utc>>,
}

/// Only the `condition_type`, `on_unknown`, `glucose_freshness_minutes` and
/// `enabled_at` are checked, not the body.
impl TryFrom<WireRule> for Rule {
    type Error = String;

    fn try_from(w: WireRule) -> Result<Self, String> {
        let condition_type = ConditionKind::from_wire(&w.condition_type).ok_or_else(|| {
            format!(
                "unknown condition_type '{}'",
                w.condition_type.escape_default()
            )
        })?;
        let on_unknown = match w.on_unknown.as_deref() {
            None => OnUnknown::Hold,
            Some(word) => OnUnknown::from_name(word)
                .ok_or_else(|| format!("unknown on_unknown '{}'", word.escape_default()))?,
        };
        let glucose_freshness = match w.glucose_freshness_minutes {
            None => DEFAULT_GLUCOSE_FRESHNESS,
            Some(minutes) if (1..=MAX_GLUCOSE_FRESHNESS_MINUTES).contains(&minutes) => {
                TimeDelta::minutes(minutes)
            }
            Some(_) => {
                return Err(
                    "glucose_freshness_minutes must be a whole number of minutes from 1 to 1440"
                        .into(),
                );
            }
        };
        let enabled_at = opt_ts(w.enabled_at, "enabled_at")?;
        Ok(Rule {
            id: w.id,
            condition_type,
            condition_params: w.condition_params,
            hysteresis_minutes: w.hysteresis_minutes,
            auto_resolve_enabled: w.auto_resolve_enabled,
            auto_resolve_params: w.auto_resolve_params,
            on_unknown,
            glucose_freshness,
            enabled_at,
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
    pub root: Truth,
    /// When the root is unknown, the facts whose unknown reached it, each
    /// with its reason (engine-semantics.md §2.4); empty when it is known.
    pub unknown: UnknownFacts,
    /// Each leaf evaluated alone, indexed by leaf id, when requested.
    pub leaves: Option<Vec<Truth>>,
    /// What each true `predicted`, `eventual_glucose` and `carbs_required`
    /// leaf of the body under an even number of `not`s met its comparison
    /// with, by leaf id, whether or not leaves are logged
    /// (engine-semantics.md §3).
    pub met_by: Vec<(usize, ConditionKind, MetPoint)>,
    pub transition: Transition,
    /// This evaluation's hold reached [`HOLD_BOUND`](crate::excursion::HOLD_BOUND):
    /// once per hold (engine-semantics.md §6.4).
    pub held_too_long: bool,
    pub tracker: Option<TrackerState>,
    pub auto_resolved: bool,
    /// Timer mutations from the root evaluation then auto-resolve, in order.
    pub timer_ops: Vec<TimerOp>,
    /// When an evaluation from the state this one left can next differ with
    /// only `now` advancing (engine-semantics.md §5.3); `None` when none can.
    pub next_change_at: Option<DateTime<Utc>>,
    /// The anchors and state instants it read beyond the tolerance ahead of
    /// `now` (engine-semantics.md §5.4).
    pub clock_anomaly: ClockAnomalies,
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
        o.insert("at".into(), format_instant(self.at).into());
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
        e.root.write(&mut o, "root");
        if e.root.is_unknown() {
            o.insert("unknown_facts".into(), e.unknown.to_json());
        }
        if let Some(leaves) = &e.leaves {
            let leaves = leaves.iter().enumerate();
            o.insert(
                "leaves".into(),
                leaves
                    .map(|(leaf_id, value)| {
                        let mut leaf = Map::new();
                        leaf.insert("leaf_id".into(), leaf_id.into());
                        value.write(&mut leaf, "value");
                        Value::Object(leaf)
                    })
                    .collect(),
            );
        }
        if !e.met_by.is_empty() {
            o.insert(
                "met_by".into(),
                e.met_by
                    .iter()
                    .map(|&(leaf_id, kind, point)| point.to_json(leaf_id, kind))
                    .collect(),
            );
        }
        o.insert("transition".into(), e.transition.kind.wire().into());
        if let Some(reason) = e.transition.close_reason {
            o.insert("close_reason".into(), reason.wire().into());
        }
        if e.held_too_long {
            o.insert("held_too_long".into(), true.into());
        }
        if let Some(tracker) = &e.tracker {
            let mut t = Map::new();
            t.insert("state".into(), tracker.state.wire().into());
            if let Some(excursion) = tracker.active_excursion {
                t.insert("excursion".into(), excursion.into());
            }
            if let Some(at) = tracker.hysteresis_started_at {
                t.insert("hysteresis_started_at".into(), format_instant(at).into());
            }
            if tracker.awaiting_rearm {
                t.insert("awaiting_rearm".into(), true.into());
            }
            if let Some(at) = tracker.held_since {
                t.insert("held_since".into(), format_instant(at).into());
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
        if let Some(at) = e.next_change_at {
            o.insert("next_change_at".into(), format_instant(at).into());
        }
        e.clock_anomaly.write(&mut o);
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
    let first_evaluation = !state.timers.has_any(rule.id)
        && state
            .tracker
            .state(rule.id)
            .is_none_or(TrackerState::holds_only_a_hold);
    let excursion_open = state.tracker.excursion_open(rule.id);
    let outcome = evaluate_body(
        rule,
        body,
        ctx,
        now,
        &mut state.timers,
        first_evaluation.then_some(rule.enabled_at).flatten(),
        excursion_open,
        log_leaves,
    );
    let mut clock_anomaly = outcome.clock_anomaly;
    let config = TrackerRuleConfig {
        hysteresis_minutes: rule.hysteresis_minutes,
    };

    // Awaiting re-arm, the resolve tree is read before the tracker, and is
    // then the evaluation's only read of it (engine-semantics.md §6.3). Only
    // a known false re-arms, and the tracker keeps the hold on a true one.
    let awaiting = state.tracker.awaiting_rearm(rule.id);
    let auto_resolve_met = awaiting
        && rule.auto_resolve_enabled
        && !auto_resolve_truth(
            rule,
            rule.parse_auto_resolve().as_ref(),
            ctx,
            now,
            &mut state.timers,
            &mut clock_anomaly,
        )
        .is_false();
    let met = rule.on_unknown.resolve(outcome.root);
    if met != Some(true) {
        clock_anomaly.note(
            state
                .tracker
                .state(rule.id)
                .filter(|s| s.state == TrackerStateKind::Hysteresis)
                .and_then(|s| hysteresis_window(s, rule.hysteresis_minutes))
                .and_then(|w| w.ahead(now)),
        );
    }
    let (transition, held_too_long) = match met {
        Some(met) => (
            state
                .tracker
                .process_evaluation(rule.id, config, met, auto_resolve_met, now),
            false,
        ),
        None => {
            clock_anomaly.note(
                state
                    .tracker
                    .state(rule.id)
                    .and_then(TrackerState::hold_bound)
                    .and_then(|bound| bound.ahead(now)),
            );
            let held = state.tracker.process_held(rule.id, config, now);
            (held.transition, held.held_too_long)
        }
    };
    let auto_resolved = !awaiting
        && rule.auto_resolve_enabled
        && try_auto_resolve(rule, ctx, now, state, &mut clock_anomaly);
    let next_change_at = next_change_at(rule, body, ctx, now, state, excursion_open);

    RuleOutcome {
        rule_id: rule.id,
        evaluation: Some(Evaluation {
            root: outcome.root,
            unknown: outcome.unknown,
            leaves: outcome.leaves,
            met_by: outcome.met_by,
            transition,
            held_too_long,
            tracker: state.tracker.state(rule.id).copied(),
            auto_resolved,
            timer_ops: state.timers.drain_ops(),
            next_change_at,
            clock_anomaly,
        }),
    }
}

/// A rule body's truth for one tick.
pub(crate) struct BodyOutcome {
    pub(crate) root: Truth,
    /// The facts that leave the root unknown.
    pub(crate) unknown: UnknownFacts,
    pub(crate) leaves: Option<Vec<Truth>>,
    pub(crate) met_by: Vec<(usize, ConditionKind, MetPoint)>,
    pub(crate) clock_anomaly: ClockAnomalies,
}

/// The root truth of a body read by [`Rule::parse_body`] and, when
/// `log_leaves`, each leaf alone. `first_evaluation_bound` is
/// [`Env::first_evaluation_bound`] and `excursion_open`
/// [`Env::excursion_open`].
#[expect(
    clippy::too_many_arguments,
    reason = "each is one input of the evaluation the driver composes"
)]
pub(crate) fn evaluate_body(
    rule: &Rule,
    body: &Node,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    timers: &mut TimerStore,
    first_evaluation_bound: Option<DateTime<Utc>>,
    excursion_open: bool,
    log_leaves: bool,
) -> BodyOutcome {
    let wire = rule.condition_type.name();
    let mut env = rule_env(rule, ctx, now, timers)
        .with_first_evaluation_bound(first_evaluation_bound)
        .with_excursion_open(excursion_open);
    let (root, unknown) = eval_payload_unknown(body.payload(), wire, &mut env);

    // Leaves evaluate alone, at the rule's root path, each with its polarity
    // in the tree so a margined leaf logs the limit the root read;
    // a leaf touches no timers.
    let polarized = collect_leaves_with_polarity(body);
    let leaves = log_leaves.then(|| {
        polarized
            .iter()
            .map(|&(leaf, negated)| {
                env.negated = negated;
                eval_node(leaf, wire, &mut env)
            })
            .collect()
    });
    // A leaf under an odd number of `not`s that is true argues against the
    // rule, so it never names what the rule met.
    env.negated = false;
    let met_by = polarized
        .iter()
        .enumerate()
        .filter(|&(_, &(_, negated))| !negated)
        .filter_map(|(leaf_id, &(leaf, _))| {
            met_point(leaf.payload(), &mut env).map(|point| (leaf_id, leaf.kind(), point))
        })
        .collect();
    BodyOutcome {
        root,
        unknown,
        leaves,
        met_by,
        clock_anomaly: env.into_clock_anomaly(),
    }
}

/// Only while an excursion is active or in hysteresis; one that the
/// auto-resolve tree holds for force-closes the excursion. An unknown tree
/// does not resolve.
fn try_auto_resolve(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
    clock_anomaly: &mut ClockAnomalies,
) -> bool {
    if state.tracker.active_excursion_id(rule.id).is_none()
        || !auto_resolve_truth(
            rule,
            rule.parse_auto_resolve().as_ref(),
            ctx,
            now,
            &mut state.timers,
            clock_anomaly,
        )
        .is_true()
    {
        return false;
    }
    let transition = state
        .tracker
        .force_close(rule.id, CloseReason::AutoResolve, now);
    transition.kind == TransitionType::ExcursionClosed
}

/// An auto-resolve tree read by [`Rule::parse_auto_resolve`] evaluated at
/// the `auto_resolve` root, adding the anchors it read ahead of the clock to
/// `clock_anomaly`; no tree is false.
pub(crate) fn auto_resolve_truth(
    rule: &Rule,
    tree: Option<&Node>,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    timers: &mut TimerStore,
    clock_anomaly: &mut ClockAnomalies,
) -> Truth {
    let Some(tree) = tree else {
        return Truth::False;
    };
    let mut env = rule_env(rule, ctx, now, timers);
    let truth = eval_node(tree, AUTO_RESOLVE_ROOT, &mut env);
    clock_anomaly.merge(env.into_clock_anomaly());
    truth
}

fn rule_env<'a>(
    rule: &Rule,
    ctx: &'a SensorContext,
    now: DateTime<Utc>,
    timers: &'a mut TimerStore,
) -> Env<'a> {
    Env::new(now, rule.id, ctx, timers)
        .with_glucose_freshness(rule.glucose_freshness)
        .with_enabled_at(rule.enabled_at)
}
