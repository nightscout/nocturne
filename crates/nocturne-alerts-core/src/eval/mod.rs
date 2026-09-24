//! Node dispatch and container evaluation (engine-semantics.md §2.4, §3).
//!
//! Evaluation cannot fail: the shapes whose evaluation fails (§1.4) are
//! rejected when a tree is parsed. Anything else malformed, such as an unknown
//! kind, operator or direction, or a container with no child, is `false`,
//! which `not` inverts.

mod clock;
mod device;
mod glucose;
mod insulin;
mod signal;
mod spans;

use chrono::{DateTime, Utc};
use rust_decimal::Decimal;
use uuid::Uuid;

use crate::compare::{Unit, decimal_from_f64_cs, elapsed};
use crate::context::SensorContext;
use crate::enums::{CmpOp, CompositeOp, holds};
use crate::model::{ConditionKind, Node, Payload};
use crate::paths::node_child_path;
use crate::sustained::{TimerStore, eval_sustained};

/// Per-evaluation environment: the clock instant, the rule whose timers are
/// keyed, the sensor context, and the mutable timer store.
pub struct Env<'a> {
    pub(crate) now: DateTime<Utc>,
    pub(crate) rule_id: Uuid,
    pub(crate) ctx: &'a SensorContext,
    pub(crate) timers: &'a mut TimerStore,
}

impl<'a> Env<'a> {
    pub fn new(
        now: DateTime<Utc>,
        rule_id: Uuid,
        ctx: &'a SensorContext,
        timers: &'a mut TimerStore,
    ) -> Self {
        Self {
            now,
            rule_id,
            ctx,
            timers,
        }
    }

    /// Whether `since` is at least `minutes` ago, in fractional minutes.
    pub(crate) fn held_for(&self, since: DateTime<Utc>, minutes: i32) -> bool {
        elapsed(self.now, since, Unit::Minutes).is_some_and(|m| m >= f64::from(minutes))
    }

    /// The time since `anchor` in `unit`, converted to decimal
    /// (engine-semantics.md §1.3), compared against `threshold`.
    fn compare_elapsed(
        &self,
        anchor: DateTime<Utc>,
        unit: Unit,
        op: Option<CmpOp>,
        threshold: impl Into<Decimal>,
    ) -> bool {
        let actual = elapsed(self.now, anchor, unit).and_then(decimal_from_f64_cs);
        holds(op, actual, threshold.into())
    }

    /// Whether a span's presence matches `is_active` and, on the active side,
    /// it has held for `for_minutes` when that is set.
    fn active_for(
        &self,
        is_active: bool,
        for_minutes: Option<i32>,
        started_at: Option<DateTime<Utc>>,
    ) -> bool {
        match started_at {
            Some(at) if is_active => for_minutes.is_none_or(|m| self.held_for(at, m)),
            Some(_) => false,
            None => !is_active,
        }
    }
}

/// Evaluates a condition node at `path`. `None` (a JSON-null child slot)
/// evaluates false.
pub fn eval_node(node: Option<&Node>, path: &str, env: &mut Env) -> bool {
    node.and_then(Node::dispatch)
        .is_some_and(|payload| eval_payload(&payload, path, env))
}

/// Evaluates a payload as the node at `path`.
pub fn eval_payload(payload: &Payload, path: &str, env: &mut Env) -> bool {
    match payload {
        Payload::Threshold(p) => glucose::threshold(p, env),
        Payload::RateOfChange(p) => glucose::rate_of_change(p, env),
        Payload::SignalLoss(p) => signal::signal_loss(p, env),
        Payload::Composite(p) => composite(p, path, env),
        Payload::Not(p) => not(p, path, env),
        Payload::Sustained(p) => eval_sustained(p, path, env),
        Payload::Staleness(p) => glucose::staleness(p, env),
        Payload::Predicted(p) => glucose::predicted(p, env),
        Payload::Trend(p) => glucose::trend(p, env),
        Payload::TimeOfDay(p) => clock::time_of_day(p, env),
        Payload::Iob(p) => insulin::iob(p, env),
        Payload::Cob(p) => insulin::cob(p, env),
        Payload::Reservoir(p) => insulin::reservoir(p, env),
        Payload::SiteAge(p) => device::site_age(p, env),
        Payload::SensorAge(p) => device::sensor_age(p, env),
        Payload::AlertState(p) => spans::alert_state(p, env),
        Payload::LoopStale(p) => device::loop_stale(p, env.ctx.last_aps_cycle_at, env),
        Payload::LoopEnactionStale(p) => device::loop_stale(p, env.ctx.last_aps_enacted_at, env),
        Payload::PumpSuspended(p) => device::pump_suspended(p, env),
        Payload::PumpBattery(p) => device::pump_battery(p, env),
        Payload::TempBasal(p) => insulin::temp_basal(p, env),
        Payload::UploaderBattery(p) => device::uploader_battery(p, env),
        Payload::OverrideActive(p) => spans::override_active(p, env),
        Payload::SensitivityRatio(p) => device::sensitivity_ratio(p, env),
        Payload::DoNotDisturb(p) => spans::do_not_disturb(p, env),
        Payload::GlucoseBucket(p) => glucose::glucose_bucket(p, env),
        Payload::TimeSinceLastCarb(p) => clock::time_since(p, env.ctx.last_carb_at, env),
        Payload::TimeSinceLastBolus(p) => clock::time_since(p, env.ctx.last_bolus_at, env),
        Payload::DayOfWeek(p) => clock::day_of_week(p, env),
        Payload::PumpState(p) => spans::pump_state(p, env),
        Payload::StateSpanActive(p) => spans::state_span_active(p, env),
        Payload::SleepSessionActive(p) => spans::sleep_session_active(p, env),
        Payload::TrackerAge(p) => device::tracker_age(p, env),
    }
}

/// Evaluates `kind` with the given payload, or its defaults when absent.
pub fn eval_kind(
    kind: ConditionKind,
    payload: Option<&Payload>,
    path: &str,
    env: &mut Env,
) -> bool {
    match payload {
        Some(p) => eval_payload(p, path, env),
        None => eval_payload(&Payload::default_for(kind), path, env),
    }
}

/// `and` / `or` short-circuit in document order; a child the short-circuit
/// skips is not evaluated at all, which its sustained timers show. An absent
/// or empty list, or an unknown operator, is false.
fn composite(p: &crate::model::CompositePayload, path: &str, env: &mut Env) -> bool {
    let Some(conditions) = p.conditions.as_deref().filter(|c| !c.is_empty()) else {
        return false;
    };
    match p.operator.value {
        Some(CompositeOp::And) => {
            for (i, child) in conditions.iter().enumerate() {
                if !eval_composite_child(child.as_ref(), i, path, env) {
                    return false;
                }
            }
            true
        }
        Some(CompositeOp::Or) => {
            for (i, child) in conditions.iter().enumerate() {
                if eval_composite_child(child.as_ref(), i, path, env) {
                    return true;
                }
            }
            false
        }
        None => false,
    }
}

fn eval_composite_child(child: Option<&Node>, index: usize, path: &str, env: &mut Env) -> bool {
    let Some(node) = child else {
        return false;
    };
    eval_node(Some(node), &node_child_path(path, index, Some(node)), env)
}

/// A missing child is false, not true; otherwise the child inverted, so
/// `not` over an unknown kind is true.
fn not(p: &crate::model::NotPayload, path: &str, env: &mut Env) -> bool {
    let Some(child) = &p.child else {
        return false;
    };
    !eval_node(Some(child), &node_child_path(path, 0, Some(child)), env)
}
