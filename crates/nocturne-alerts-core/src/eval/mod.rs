//! Node dispatch and container evaluation (engine-semantics.md §2.4, §3).
//!
//! Evaluation cannot fail. The tree shapes whose evaluation is defined to
//! fail (engine-semantics.md §1.4) are rejected by [`Node::parse`] and
//! [`crate::model::parse_payload`] before a tree gets here; a tree built with
//! the structural parse alone evaluates those shapes `false`. Everything else
//! that is malformed — an unknown kind, operator or direction, a container
//! with no child — evaluates `false` too, which `not` inverts.

mod clock;
mod device;
mod glucose;
mod insulin;
mod signal;
mod spans;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::context::SensorContext;
use crate::enums::CompositeOp;
use crate::model::{ConditionKind, Node, Payload};
use crate::paths::node_child_path;
use crate::sustained::{TimerStore, eval_sustained};

/// Per-evaluation environment: the clock instant, the rule whose timers are
/// keyed, the sensor context, and the mutable timer store.
pub struct Env<'a> {
    pub now: DateTime<Utc>,
    pub rule_id: Uuid,
    pub ctx: &'a SensorContext,
    pub timers: &'a mut TimerStore,
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
        Payload::LoopStale(p) => device::loop_stale(p, env),
        Payload::LoopEnactionStale(p) => device::loop_enaction_stale(p, env),
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

/// `CompositeEvaluator`: operator lowercased, only `and`/`or` recognised,
/// document-order short-circuit. Children skipped by short-circuit are not
/// evaluated at all (observable through sustained timers).
fn composite(p: &crate::model::CompositePayload, path: &str, env: &mut Env) -> bool {
    // C# checks `condition is null || condition.Conditions.Count == 0` first:
    // a null list NREs (observed false), an empty list is false.
    let Some(conditions) = &p.conditions else {
        return false;
    };
    if conditions.is_empty() {
        return false;
    }
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

/// `NotEvaluator`: missing child → false (not true). Otherwise inverts the
/// child — so `not` over an unknown child kind yields true. **[normative]**
fn not(p: &crate::model::NotPayload, path: &str, env: &mut Env) -> bool {
    let Some(child) = &p.child else {
        return false;
    };
    !eval_node(Some(child), &node_child_path(path, 0, Some(child)), env)
}
