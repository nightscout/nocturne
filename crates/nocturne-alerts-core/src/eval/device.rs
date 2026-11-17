//! Device/loop leaves: site_age, sensor_age, tracker_age, loop_stale,
//! loop_enaction_stale, pump_suspended, pump_battery, uploader_battery,
//! sensitivity_ratio.

use chrono::{DateTime, TimeDelta, Utc};

use super::Env;
use crate::compare::Unit;
use crate::context::TrackerReference;
use crate::enums::{CmpOp, holds};
use crate::model::{ActiveForPayload, ComparePayload, MinutesComparePayload, TrackerAgePayload};

/// Site age in hours; no site change is false.
pub(super) fn site_age(p: &ComparePayload, env: &Env) -> bool {
    env.ctx
        .last_site_change_at
        .is_some_and(|at| env.compare_elapsed(at, Unit::Hours, p.operator, p.value))
}

/// Sensor age in days; no sensor start is false.
pub(super) fn sensor_age(p: &ComparePayload, env: &Env) -> bool {
    env.ctx
        .last_sensor_start_at
        .is_some_and(|at| env.compare_elapsed(at, Unit::Days, p.operator, p.value))
}

/// Minutes since the active tracker instance's reference timestamp (negative
/// before a scheduled event). No active instance is false: a tracker that is
/// not running has no age, unlike time_since_last_*'s cold-start infinity.
/// For a duration run, a threshold that already held when the run was recorded
/// is false for that run, so a run entered after the fact does not alert at
/// once for what it has already passed.
pub(super) fn tracker_age(p: &TrackerAgePayload, env: &Env) -> bool {
    env.ctx
        .active_trackers
        .get(&p.tracker_definition_id)
        .is_some_and(|t| {
            !held_when_recorded(p, t)
                && env.compare_elapsed(t.reference_at, Unit::Minutes, p.operator, p.minutes)
        })
}

/// Whether the threshold held at the recorded time: `>=` from its due time,
/// `>` only after it. A scheduled (event) run is never held back, since
/// booking an appointment is when its reminders become news. `<`, `<=` and
/// `==` hold from the start or for one moment, and are never held back.
fn held_when_recorded(p: &TrackerAgePayload, t: &TrackerReference) -> bool {
    if t.scheduled {
        return false;
    }
    let Some(recorded) = t.recorded_at else {
        return false;
    };
    let Some(due) = t
        .reference_at
        .checked_add_signed(TimeDelta::minutes(i64::from(p.minutes)))
    else {
        return false;
    };
    match p.operator {
        CmpOp::Ge => due <= recorded,
        CmpOp::Gt => due < recorded,
        CmpOp::Lt | CmpOp::Le | CmpOp::Eq => false,
    }
}

/// loop_stale against the last cycle, loop_enaction_stale against the last
/// enactment; both guarded by `has_ever_aps_cycled`. A null anchor is false
/// (no infinity convention, unlike staleness).
pub(super) fn loop_stale(
    p: &MinutesComparePayload,
    anchor: Option<DateTime<Utc>>,
    env: &Env,
) -> bool {
    env.ctx.has_ever_aps_cycled
        && anchor
            .is_some_and(|at| env.compare_elapsed(at, Unit::Minutes, p.operator, p.minutes.get()))
}

/// Guarded by `has_ever_pump_snapshot`.
pub(super) fn pump_suspended(p: &ActiveForPayload, env: &Env) -> bool {
    env.ctx.has_ever_pump_snapshot
        && env.active_for(
            p.is_active,
            p.for_minutes,
            env.ctx.active_pump_suspension.map(|s| s.started_at),
        )
}

pub(super) fn pump_battery(p: &ComparePayload, env: &Env) -> bool {
    env.ctx.has_ever_pump_snapshot && holds(p.operator, env.ctx.pump_battery_percent, p.value)
}

pub(super) fn uploader_battery(p: &ComparePayload, env: &Env) -> bool {
    env.ctx.has_ever_uploader_snapshot
        && holds(p.operator, env.ctx.uploader_battery_percent, p.value)
}

pub(super) fn sensitivity_ratio(p: &ComparePayload, env: &Env) -> bool {
    env.ctx.has_ever_aps_sensitivity && holds(p.operator, env.ctx.sensitivity_ratio, p.value)
}
