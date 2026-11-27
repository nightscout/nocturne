//! Device/loop leaves: site_age, sensor_age, tracker_age, loop_stale,
//! loop_enaction_stale, pump_suspended, pump_battery, uploader_battery,
//! transmitter_battery, sensitivity_ratio.

use chrono::{DateTime, Utc};

use super::elapsed::{ClockLeaf, active_for, minutes, whole_minutes};
use super::{Env, Held, unknown_by};
use crate::clock_anomaly::ClockAnchor;
use crate::context::{SensorContext, TrackerReference};
use crate::enums::{CmpOp, TransmitterBatteryMetric, holds};
use crate::facts::{ContextFact, ContextFact as F};
use crate::model::{
    ActiveForPayload, ComparePayload, MinutesComparePayload, TrackerAgePayload,
    TransmitterBatteryPayload,
};
use crate::truth::Truth;
use crate::unknown::UnknownReason as R;

/// The time since the site change; no site change is unknown.
pub(super) fn site_age(p: &MinutesComparePayload, ctx: &SensorContext) -> ClockLeaf {
    ctx.last_site_change_at
        .map_or(ClockLeaf::Unknown(F::DeviceAges, R::Absent), |at| {
            minutes(at, Some(ClockAnchor::LastSiteChangeAt), p.operator, p.minutes.get())
        })
}

/// The time since the sensor start; no sensor start is unknown.
pub(super) fn sensor_age(p: &MinutesComparePayload, ctx: &SensorContext) -> ClockLeaf {
    ctx.last_sensor_start_at
        .map_or(ClockLeaf::Unknown(F::DeviceAges, R::Absent), |at| {
            minutes(at, Some(ClockAnchor::LastSensorStartAt), p.operator, p.minutes.get())
        })
}

/// Minutes since the active tracker instance's reference timestamp (negative
/// before a scheduled event). No active instance is false: a tracker that is
/// not running has no age, unlike time_since_last_*'s cold-start infinity.
/// For a duration run, a threshold that already held when the run was recorded
/// is false for that run, so a run entered after the fact does not alert at
/// once for what it has already passed. A scheduled time is meant to be ahead
/// of `now`; a start is not, and one beyond the tolerance ahead is unknown
/// before it is held back (engine-semantics.md §5.4).
pub(super) fn tracker_age(
    p: &TrackerAgePayload,
    ctx: &SensorContext,
    now: DateTime<Utc>,
) -> ClockLeaf {
    let Some(t) = ctx.active_trackers.get(&p.tracker_definition_id) else {
        return ClockLeaf::Fixed(Truth::False);
    };
    let leaf = minutes(
        t.reference_at,
        (!t.scheduled).then_some(ClockAnchor::ActiveTrackers),
        p.operator,
        p.minutes,
    );
    let ahead = leaf.elapsed().is_some_and(|e| e.ahead(now).is_some());
    if held_when_recorded(p, t) && !ahead {
        ClockLeaf::Fixed(Truth::False)
    } else {
        leaf
    }
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
    let Some(due) = t.reference_at.checked_add_signed(whole_minutes(p.minutes)) else {
        return false;
    };
    match p.operator {
        CmpOp::Ge => due <= recorded,
        CmpOp::Gt => due < recorded,
        CmpOp::Lt | CmpOp::Le | CmpOp::Eq => false,
    }
}

/// loop_stale against the last cycle, guarded by `has_ever_aps_cycled`;
/// loop_enaction_stale against the last enactment, unguarded (`guard` true). The
/// guard false or a null anchor is unknown (no infinity convention, unlike
/// staleness).
pub(super) fn loop_stale(
    p: &MinutesComparePayload,
    anchor: Option<DateTime<Utc>>,
    source: ClockAnchor,
    guard: bool,
) -> ClockLeaf {
    match anchor {
        Some(at) if guard => minutes(at, Some(source), p.operator, p.minutes.get()),
        _ => ClockLeaf::Unknown(F::Aps, R::Absent),
    }
}

/// Guarded by `has_ever_pump_snapshot`: unknown until a pump has reported.
pub(super) fn pump_suspended(p: &ActiveForPayload, ctx: &SensorContext) -> ClockLeaf {
    if !ctx.has_ever_pump_snapshot {
        return ClockLeaf::Unknown(F::Pump, R::Absent);
    }
    active_for(
        p.is_active,
        p.for_minutes,
        ctx.active_pump_suspension.map(|s| s.started_at),
        ClockAnchor::ActivePumpSuspension,
    )
}

/// The `has_ever_*` guard false is unknown, as is an absent reading.
fn guarded<T: PartialOrd>(
    fact: ContextFact,
    guard: bool,
    op: CmpOp,
    actual: Option<T>,
    threshold: T,
) -> Held {
    let truth = if guard {
        holds(op, actual, threshold)
    } else {
        Truth::Unknown
    };
    unknown_by(truth, fact, R::Absent)
}

pub(super) fn pump_battery(p: &ComparePayload, env: &Env) -> Held {
    guarded(
        F::Pump,
        env.ctx.has_ever_pump_snapshot,
        p.operator,
        env.ctx.pump_battery_percent,
        env.limit(p),
    )
}

pub(super) fn uploader_battery(p: &ComparePayload, env: &Env) -> Held {
    guarded(
        F::Uploader,
        env.ctx.has_ever_uploader_snapshot,
        p.operator,
        env.ctx.uploader_battery_percent,
        env.limit(p),
    )
}

/// No `has_ever_*` guard: a reading no uploader has reported is absent, and
/// unknown.
pub(super) fn transmitter_battery(p: &TransmitterBatteryPayload, env: &Env) -> Held {
    let actual = match p.metric {
        TransmitterBatteryMetric::VoltageA => env.ctx.transmitter_voltage_a,
        TransmitterBatteryMetric::VoltageB => env.ctx.transmitter_voltage_b,
        TransmitterBatteryMetric::Resistance => env.ctx.transmitter_resistance,
        TransmitterBatteryMetric::Days => env.ctx.transmitter_days,
    };
    unknown_by(
        holds(p.operator, actual, env.limit(p)),
        F::Transmitter,
        R::Absent,
    )
}

pub(super) fn sensitivity_ratio(p: &ComparePayload, env: &Env) -> Held {
    guarded(
        F::Aps,
        env.ctx.has_ever_aps_sensitivity,
        p.operator,
        env.ctx.sensitivity_ratio,
        env.limit(p),
    )
}
