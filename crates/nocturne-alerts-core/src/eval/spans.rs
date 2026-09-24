//! State-span and cross-alert leaves: alert_state, override_active,
//! do_not_disturb, pump_state, state_span_active, sleep_session_active.

use super::Env;
use crate::enums::{AlertStateKind, EnumValue, StateSpanCategory};
use crate::model::{
    ActiveForPayload, AlertStatePayload, PumpStatePayload, SleepSessionPayload, StateSpanPayload,
};

/// Cross-alert state. `firing`: the snapshot's state is "firing" (ASCII case
/// ignored); `unacknowledged`: firing and not acknowledged; `acknowledged`:
/// acknowledged whatever the state. `for_minutes` holds from the
/// acknowledgement for `acknowledged`, from the trigger otherwise.
pub(super) fn alert_state(p: &AlertStatePayload, env: &Env) -> bool {
    let Some(snapshot) = env.ctx.active_alerts.get(&p.alert_id) else {
        return false;
    };
    let firing = snapshot.state.eq_ignore_ascii_case("firing");
    let anchor = match p.state.value {
        Some(AlertStateKind::Firing) if firing => Some(snapshot.triggered_at),
        Some(AlertStateKind::Unacknowledged) if firing && snapshot.acknowledged_at.is_none() => {
            Some(snapshot.triggered_at)
        }
        Some(AlertStateKind::Acknowledged) => snapshot.acknowledged_at,
        _ => None,
    };
    anchor.is_some_and(|at| p.for_minutes.is_none_or(|m| env.held_for(at, m)))
}

/// No `has_ever_*` guard: no override is the legitimate "no override" state.
pub(super) fn override_active(p: &ActiveForPayload, env: &Env) -> bool {
    env.active_for(
        p.is_active,
        p.for_minutes,
        env.ctx.active_override.map(|s| s.started_at),
    )
}

/// A null snapshot means DND is off.
pub(super) fn do_not_disturb(p: &ActiveForPayload, env: &Env) -> bool {
    env.active_for(
        p.is_active,
        p.for_minutes,
        env.ctx.active_do_not_disturb.map(|s| s.started_at),
    )
}

/// `is_active: false` is true whenever the active mode differs from the
/// configured one, including when no mode is active.
pub(super) fn pump_state(p: &PumpStatePayload, env: &Env) -> bool {
    let in_mode = env
        .ctx
        .active_pump_state
        .filter(|s| EnumValue::Known(s.mode) == p.mode);
    env.active_for(p.is_active, p.for_minutes, in_mode.map(|s| s.started_at))
}

/// Generic state-span leaf. The PumpMode category is always false (pump
/// modes are `pump_state`'s). The lookup key is the exact `(category, state)`
/// pair; a null state means "any state of this category".
pub(super) fn state_span_active(p: &StateSpanPayload, env: &Env) -> bool {
    let span = match p.category {
        EnumValue::Known(StateSpanCategory::PumpMode) => return false,
        EnumValue::Known(category) => env.ctx.active_state_spans.get(&(category, p.state.clone())),
        EnumValue::Undefined(_) => None,
    };
    env.active_for(p.is_active, p.for_minutes, span.map(|s| s.started_at))
}

/// `is_active` true fires while a sleep session is active, false while none is.
pub(super) fn sleep_session_active(p: &SleepSessionPayload, env: &Env) -> bool {
    env.ctx.sleep_session_active == p.is_active
}
