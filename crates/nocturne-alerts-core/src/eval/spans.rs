//! State-span and cross-alert leaves: alert_state, override_active,
//! do_not_disturb, pump_state, state_span_active, sleep_session_active.

use super::Env;
use super::elapsed::{ClockLeaf, Elapsed, active_for};
use crate::clock_anomaly::ClockAnchor;
use crate::context::SensorContext;
use crate::enums::AlertStateKind;
use crate::model::{
    ActiveForPayload, AlertStatePayload, PumpStatePayload, SleepSessionPayload, StateSpanPayload,
};
use crate::truth::Truth;

/// Cross-alert state. `firing`: the snapshot's state is "firing" (ASCII case
/// ignored); `unacknowledged`: firing and not acknowledged; `acknowledged`:
/// acknowledged whatever the state. `for_minutes` holds from the
/// acknowledgement for `acknowledged`, from the trigger otherwise.
pub(super) fn alert_state(p: &AlertStatePayload, ctx: &SensorContext) -> ClockLeaf {
    let Some(snapshot) = ctx.active_alerts.get(&p.alert_id) else {
        return ClockLeaf::Fixed(Truth::False);
    };
    let firing = snapshot.state.eq_ignore_ascii_case("firing");
    let anchor = match p.state {
        AlertStateKind::Firing if firing => Some(snapshot.triggered_at),
        AlertStateKind::Unacknowledged if firing && snapshot.acknowledged_at.is_none() => {
            Some(snapshot.triggered_at)
        }
        AlertStateKind::Acknowledged => snapshot.acknowledged_at,
        AlertStateKind::Firing | AlertStateKind::Unacknowledged => None,
    };
    match (anchor, p.for_minutes) {
        (None, _) => ClockLeaf::Fixed(Truth::False),
        (Some(_), None) => ClockLeaf::Fixed(Truth::True),
        (Some(at), Some(m)) => {
            ClockLeaf::Measured(Elapsed::held_for(at, m, ClockAnchor::ActiveAlerts))
        }
    }
}

/// No `has_ever_*` guard: no override is the legitimate "no override" state.
pub(super) fn override_active(p: &ActiveForPayload, ctx: &SensorContext) -> ClockLeaf {
    active_for(
        p.is_active,
        p.for_minutes,
        ctx.active_override.map(|s| s.started_at),
        ClockAnchor::ActiveOverride,
    )
}

/// A null snapshot means DND is off.
pub(super) fn do_not_disturb(p: &ActiveForPayload, ctx: &SensorContext) -> ClockLeaf {
    active_for(
        p.is_active,
        p.for_minutes,
        ctx.active_do_not_disturb.map(|s| s.started_at),
        ClockAnchor::ActiveDoNotDisturb,
    )
}

/// Reads the entry for the mode it names: the latest start when the mode is
/// repeated, so a repeat never makes `for_minutes` hold sooner. `is_active:
/// false` is true whenever that mode is not active, including when no mode is.
/// With the named mode absent, an active mode the crate cannot name may or may
/// not be it: unknown wherever the answer depends on it, and with `is_active:
/// true` and `for_minutes` false until the earliest such span has lasted that
/// long.
pub(super) fn pump_state(p: &PumpStatePayload, ctx: &SensorContext) -> ClockLeaf {
    let named = ctx
        .active_pump_modes
        .iter()
        .filter(|m| m.mode == Some(p.mode))
        .map(|m| m.started_at)
        .max();
    let unrecognised = ctx
        .active_pump_modes
        .iter()
        .filter(|m| m.mode.is_none())
        .map(|m| m.started_at)
        .min();
    match (named, unrecognised, p.for_minutes) {
        (None, Some(started_at), Some(m)) if p.is_active => ClockLeaf::UnknownOnceHeld(
            Elapsed::held_for(started_at, m, ClockAnchor::ActivePumpModes),
        ),
        (None, Some(_), _) => ClockLeaf::Fixed(Truth::Unknown),
        _ => active_for(
            p.is_active,
            p.for_minutes,
            named,
            ClockAnchor::ActivePumpModes,
        ),
    }
}

/// Generic state-span leaf. The lookup key is the exact `(category, state)`
/// pair; a null state means "any state of this category". A span of a
/// category the crate cannot name is not in the map, and cannot be the
/// category the leaf names.
pub(super) fn state_span_active(p: &StateSpanPayload, ctx: &SensorContext) -> ClockLeaf {
    let span = ctx.active_state_spans.get(&(p.category, p.state.clone()));
    active_for(
        p.is_active,
        p.for_minutes,
        span.map(|s| s.started_at),
        ClockAnchor::ActiveStateSpans,
    )
}

/// `is_active` true fires while a sleep session is active, false while none is.
pub(super) fn sleep_session_active(p: &SleepSessionPayload, env: &Env) -> Truth {
    (env.ctx.sleep_session_active == p.is_active).into()
}
