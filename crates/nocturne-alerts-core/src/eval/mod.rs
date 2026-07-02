//! Node dispatch and container evaluation.
//!
//! Mirrors `ConditionEvaluatorRegistry.EvaluateNodeAsync` + the recursive
//! container evaluators. The universal failure mode is silent-false: unknown
//! kinds, missing payloads and conditions that would throw in C# inside a
//! caught context all evaluate `false` rather than erroring. (Where C# would
//! throw *uncaught*, the scenario cannot exist in the corpus — the generator
//! itself would have crashed — so false is observably equivalent.)

mod clock;
mod device;
mod glucose;
mod insulin;
mod spans;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::context::SensorContext;
use crate::model::{ConditionKind, Node, Payload, default_payload};
use crate::paths::child_path;
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
    let Some(node) = node else {
        return false;
    };
    let Some(type_str) = node.type_str.as_deref() else {
        return false;
    };
    let Some(kind) = ConditionKind::resolve(type_str) else {
        return false;
    };
    // The payload switch in ConditionNodePayloads matches the lowercased type
    // against the canonical snake_case names only — a kind resolved through
    // the lenient enum-name path (e.g. "RateOfChange") finds no payload and
    // evaluates with constructor defaults.
    let lower = type_str.to_lowercase();
    let payload = if lower == kind.wire() {
        node.payload(&lower)
    } else {
        None
    };
    eval_kind(kind, payload, path, env)
}

/// Evaluates `kind` with the given payload (or the `{}`-defaults when absent).
pub fn eval_kind(
    kind: ConditionKind,
    payload: Option<&Payload>,
    path: &str,
    env: &mut Env,
) -> bool {
    let default;
    let payload = match payload {
        Some(p) => p,
        None => {
            default = default_payload(kind);
            &default
        }
    };
    match (kind, payload) {
        (ConditionKind::Threshold, Payload::Threshold(p)) => glucose::threshold(p, env),
        (ConditionKind::RateOfChange, Payload::RateOfChange(p)) => glucose::rate_of_change(p, env),
        // signal_loss has no registered evaluator: false inside trees, the
        // real behaviour is a host-side sweep (§5 of the semantics doc).
        (ConditionKind::SignalLoss, _) => false,
        (ConditionKind::Composite, Payload::Composite(p)) => composite(p, path, env),
        (ConditionKind::Not, Payload::Not(p)) => not(p, path, env),
        (ConditionKind::Sustained, Payload::Sustained(p)) => eval_sustained(p, path, env),
        (ConditionKind::Staleness, Payload::Staleness(p)) => glucose::staleness(p, env),
        (ConditionKind::Predicted, Payload::Predicted(p)) => glucose::predicted(p, env),
        (ConditionKind::Trend, Payload::Trend(p)) => glucose::trend(p, env),
        (ConditionKind::TimeOfDay, Payload::TimeOfDay(p)) => clock::time_of_day(p, env),
        (ConditionKind::Iob, Payload::Compare(p)) => insulin::iob(p, env),
        (ConditionKind::Cob, Payload::Compare(p)) => insulin::cob(p, env),
        (ConditionKind::Reservoir, Payload::Compare(p)) => insulin::reservoir(p, env),
        (ConditionKind::SiteAge, Payload::Compare(p)) => device::site_age(p, env),
        (ConditionKind::SensorAge, Payload::Compare(p)) => device::sensor_age(p, env),
        (ConditionKind::AlertState, Payload::AlertState(p)) => spans::alert_state(p, env),
        (ConditionKind::LoopStale, Payload::MinutesCompare(p)) => device::loop_stale(p, env),
        (ConditionKind::LoopEnactionStale, Payload::MinutesCompare(p)) => {
            device::loop_enaction_stale(p, env)
        }
        (ConditionKind::PumpSuspended, Payload::ActiveFor(p)) => device::pump_suspended(p, env),
        (ConditionKind::PumpBattery, Payload::Compare(p)) => device::pump_battery(p, env),
        (ConditionKind::TempBasal, Payload::TempBasal(p)) => insulin::temp_basal(p, env),
        (ConditionKind::UploaderBattery, Payload::Compare(p)) => device::uploader_battery(p, env),
        (ConditionKind::OverrideActive, Payload::ActiveFor(p)) => spans::override_active(p, env),
        (ConditionKind::SensitivityRatio, Payload::Compare(p)) => device::sensitivity_ratio(p, env),
        (ConditionKind::DoNotDisturb, Payload::ActiveFor(p)) => spans::do_not_disturb(p, env),
        (ConditionKind::GlucoseBucket, Payload::GlucoseBucket(p)) => {
            glucose::glucose_bucket(p, env)
        }
        (ConditionKind::TimeSinceLastCarb, Payload::TimeSince(p)) => {
            clock::time_since(p, env.ctx.last_carb_at, env)
        }
        (ConditionKind::TimeSinceLastBolus, Payload::TimeSince(p)) => {
            clock::time_since(p, env.ctx.last_bolus_at, env)
        }
        (ConditionKind::DayOfWeek, Payload::DayOfWeek(p)) => clock::day_of_week(p, env),
        (ConditionKind::PumpState, Payload::PumpState(p)) => spans::pump_state(p, env),
        (ConditionKind::StateSpanActive, Payload::StateSpan(p)) => spans::state_span_active(p, env),
        (ConditionKind::TrackerAge, Payload::TrackerAge(p)) => device::tracker_age(p, env),
        // A payload variant can only be stored under its own kind's key, so a
        // mismatch is unreachable; fail closed regardless.
        _ => false,
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
    let Some(operator) = p.operator.as_deref() else {
        return false;
    };
    match operator.to_lowercase().as_str() {
        "and" => {
            for (i, child) in conditions.iter().enumerate() {
                if !eval_composite_child(child.as_ref(), i, path, env) {
                    return false;
                }
            }
            true
        }
        "or" => {
            for (i, child) in conditions.iter().enumerate() {
                if eval_composite_child(child.as_ref(), i, path, env) {
                    return true;
                }
            }
            false
        }
        _ => false,
    }
}

fn eval_composite_child(child: Option<&Node>, index: usize, path: &str, env: &mut Env) -> bool {
    let Some(node) = child else {
        return false;
    };
    let child_path = child_path(path, index, node.type_str.as_deref());
    eval_node(Some(node), &child_path, env)
}

/// `NotEvaluator`: missing child → false (not true). Otherwise inverts the
/// child — so `not` over an unknown child kind yields true. **[normative]**
fn not(p: &crate::model::NotPayload, path: &str, env: &mut Env) -> bool {
    let Some(child) = &p.child else {
        return false;
    };
    let child_path = child_path(path, 0, child.type_str.as_deref());
    !eval_node(Some(child), &child_path, env)
}
