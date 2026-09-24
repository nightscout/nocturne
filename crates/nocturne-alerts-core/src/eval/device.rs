//! Device/loop leaves: site_age, sensor_age, tracker_age, loop_stale,
//! loop_enaction_stale, pump_suspended, pump_battery, uploader_battery,
//! sensitivity_ratio.

use rust_decimal::Decimal;

use super::Env;
use crate::compare::{decimal_from_f64_cs, total_days, total_hours, total_minutes};
use crate::enums::holds;
use crate::model::{ActiveForPayload, ComparePayload, MinutesComparePayload, TrackerAgePayload};

/// Site age in **hours**; no site change → false.
pub(super) fn site_age(p: &ComparePayload, env: &Env) -> bool {
    let Some(changed_at) = env.ctx.last_site_change_at else {
        return false;
    };
    let age = total_hours(env.now - changed_at).and_then(decimal_from_f64_cs);
    holds(p.operator.value, age, p.value)
}

/// Sensor age in **days**; no sensor start → false.
pub(super) fn sensor_age(p: &ComparePayload, env: &Env) -> bool {
    let Some(started_at) = env.ctx.last_sensor_start_at else {
        return false;
    };
    let age = total_days(env.now - started_at).and_then(decimal_from_f64_cs);
    holds(p.operator.value, age, p.value)
}

/// Minutes since the active tracker instance's reference timestamp (negative
/// before a scheduled event). No active instance → false: a tracker that is
/// not running has no age, unlike time_since_last_*'s cold-start infinity.
pub(super) fn tracker_age(p: &TrackerAgePayload, env: &Env) -> bool {
    let Some(reference_at) = env.ctx.active_trackers.get(&p.tracker_definition_id) else {
        return false;
    };
    let minutes_since = total_minutes(env.now - *reference_at).and_then(decimal_from_f64_cs);
    holds(p.operator.value, minutes_since, Decimal::from(p.minutes))
}

/// Guarded by `has_ever_aps_cycled`; a null cycle timestamp is false (no
/// infinity convention here, unlike staleness).
pub(super) fn loop_stale(p: &MinutesComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_aps_cycled {
        return false;
    }
    let Some(cycle_at) = env.ctx.last_aps_cycle_at else {
        return false;
    };
    let minutes_since = total_minutes(env.now - cycle_at).and_then(decimal_from_f64_cs);
    holds(p.operator.value, minutes_since, Decimal::from(p.minutes))
}

/// Same shape against the enacted timestamp, deliberately guarded by
/// `has_ever_aps_cycled`.
pub(super) fn loop_enaction_stale(p: &MinutesComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_aps_cycled {
        return false;
    }
    let Some(enacted_at) = env.ctx.last_aps_enacted_at else {
        return false;
    };
    let minutes_since = total_minutes(env.now - enacted_at).and_then(decimal_from_f64_cs);
    holds(p.operator.value, minutes_since, Decimal::from(p.minutes))
}

/// Guarded by `has_ever_pump_snapshot`. `for_minutes` only applies on the
/// `is_active: true` side.
pub(super) fn pump_suspended(p: &ActiveForPayload, env: &Env) -> bool {
    if !env.ctx.has_ever_pump_snapshot {
        return false;
    }
    let Some(suspension) = env.ctx.active_pump_suspension else {
        return !p.is_active;
    };
    if !p.is_active {
        return false;
    }
    p.for_minutes.is_none_or(|for_minutes| {
        total_minutes(env.now - suspension.started_at).is_some_and(|m| m >= f64::from(for_minutes))
    })
}

pub(super) fn pump_battery(p: &ComparePayload, env: &Env) -> bool {
    env.ctx.has_ever_pump_snapshot && holds(p.operator.value, env.ctx.pump_battery_percent, p.value)
}

pub(super) fn uploader_battery(p: &ComparePayload, env: &Env) -> bool {
    env.ctx.has_ever_uploader_snapshot
        && holds(p.operator.value, env.ctx.uploader_battery_percent, p.value)
}

pub(super) fn sensitivity_ratio(p: &ComparePayload, env: &Env) -> bool {
    env.ctx.has_ever_aps_sensitivity && holds(p.operator.value, env.ctx.sensitivity_ratio, p.value)
}
