//! Device/loop leaves: site_age, sensor_age, loop_stale, loop_enaction_stale,
//! pump_suspended, pump_battery, uploader_battery, sensitivity_ratio.

use rust_decimal::Decimal;

use super::Env;
use crate::compare::{compare, decimal_from_f64_cs, total_days, total_hours, total_minutes};
use crate::model::{ActiveForPayload, ComparePayload, MinutesComparePayload, TrackerAgePayload};

/// Site age in **hours** (f64 then C# decimal cast); no site change → false.
pub(super) fn site_age(p: &ComparePayload, env: &Env) -> bool {
    let Some(changed_at) = env.ctx.last_site_change_at else {
        return false;
    };
    let Some(age) = decimal_from_f64_cs(total_hours(env.now - changed_at)) else {
        return false;
    };
    compare(age, p.operator.as_deref(), p.value)
}

/// Sensor age in **days** (f64 then C# decimal cast); no sensor start → false.
pub(super) fn sensor_age(p: &ComparePayload, env: &Env) -> bool {
    let Some(started_at) = env.ctx.last_sensor_start_at else {
        return false;
    };
    let Some(age) = decimal_from_f64_cs(total_days(env.now - started_at)) else {
        return false;
    };
    compare(age, p.operator.as_deref(), p.value)
}

/// Minutes since the active tracker instance's reference timestamp for the
/// payload's tracker definition (start for duration trackers, scheduled time
/// for event trackers — resolved by the enricher). Elapsed is negative before
/// a scheduled event. No active instance → false: a tracker that isn't
/// running has no age, deliberately opposite to time_since_last_* cold-start
/// infinity. **[normative]**
pub(super) fn tracker_age(p: &TrackerAgePayload, env: &Env) -> bool {
    let Some(reference_at) = env.ctx.active_trackers.get(&p.tracker_definition_id) else {
        return false;
    };
    let Some(minutes_since) = decimal_from_f64_cs(total_minutes(env.now - *reference_at)) else {
        return false;
    };
    compare(
        minutes_since,
        p.operator.as_deref(),
        Decimal::from(p.minutes),
    )
}

/// Guarded by `HasEverApsCycled`; a null cycle timestamp is false (no infinity
/// convention here, unlike staleness).
pub(super) fn loop_stale(p: &MinutesComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_aps_cycled {
        return false;
    }
    let Some(cycle_at) = env.ctx.last_aps_cycle_at else {
        return false;
    };
    let Some(minutes_since) = decimal_from_f64_cs(total_minutes(env.now - cycle_at)) else {
        return false;
    };
    compare(
        minutes_since,
        p.operator.as_deref(),
        Decimal::from(p.minutes),
    )
}

/// Same shape against the enacted timestamp. The cold-start guard is
/// deliberately `HasEverApsCycled`, not an enaction-specific flag. **[normative]**
pub(super) fn loop_enaction_stale(p: &MinutesComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_aps_cycled {
        return false;
    }
    let Some(enacted_at) = env.ctx.last_aps_enacted_at else {
        return false;
    };
    let Some(minutes_since) = decimal_from_f64_cs(total_minutes(env.now - enacted_at)) else {
        return false;
    };
    compare(
        minutes_since,
        p.operator.as_deref(),
        Decimal::from(p.minutes),
    )
}

/// Guarded by `HasEverPumpSnapshot`. `for_minutes` only applies on the
/// `is_active: true` side (no anchor exists otherwise — treated as a no-op).
pub(super) fn pump_suspended(p: &ActiveForPayload, env: &Env) -> bool {
    if !env.ctx.has_ever_pump_snapshot {
        return false;
    }
    let is_currently_suspended = env.ctx.active_pump_suspension.is_some();
    if is_currently_suspended != p.is_active {
        return false;
    }
    let Some(for_minutes) = p.for_minutes else {
        return true;
    };
    if !p.is_active {
        return true;
    }
    let started_at = env
        .ctx
        .active_pump_suspension
        .expect("suspension present when is_active matched")
        .started_at;
    total_minutes(env.now - started_at) >= f64::from(for_minutes)
}

pub(super) fn pump_battery(p: &ComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_pump_snapshot {
        return false;
    }
    match env.ctx.pump_battery_percent {
        Some(percent) => compare(percent, p.operator.as_deref(), p.value),
        None => false,
    }
}

pub(super) fn uploader_battery(p: &ComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_uploader_snapshot {
        return false;
    }
    match env.ctx.uploader_battery_percent {
        Some(percent) => compare(percent, p.operator.as_deref(), p.value),
        None => false,
    }
}

pub(super) fn sensitivity_ratio(p: &ComparePayload, env: &Env) -> bool {
    if !env.ctx.has_ever_aps_sensitivity {
        return false;
    }
    match env.ctx.sensitivity_ratio {
        Some(ratio) => compare(ratio, p.operator.as_deref(), p.value),
        None => false,
    }
}
