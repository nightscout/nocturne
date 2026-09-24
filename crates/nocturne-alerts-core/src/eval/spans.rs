//! State-span and cross-alert leaves: alert_state, override_active,
//! do_not_disturb, pump_state, state_span_active, sleep_session_active.

use super::Env;
use crate::compare::total_minutes;
use crate::enums::{AlertStateKind, EnumValue, StateSpanCategory};
use crate::model::{
    ActiveForPayload, AlertStatePayload, PumpStatePayload, SleepSessionPayload, StateSpanPayload,
};

/// Cross-alert state. `firing`: snapshot state equals "firing" (ci);
/// `unacknowledged`: firing and not acknowledged; `acknowledged`: acknowledged
/// regardless of state. `for_minutes` anchors at `AcknowledgedAt` for
/// acknowledged, `TriggeredAt` otherwise (f64 minutes, `>=`).
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
    anchor.is_some_and(|anchor| {
        p.for_minutes.is_none_or(|for_minutes| {
            total_minutes(env.now - anchor).is_some_and(|m| m >= f64::from(for_minutes))
        })
    })
}

/// No `HasEver*` guard: absence of an override is the legitimate "no override"
/// state. `for_minutes` is a no-op on the inactive side.
pub(super) fn override_active(p: &ActiveForPayload, env: &Env) -> bool {
    let is_currently_active = env.ctx.active_override.is_some();
    if is_currently_active != p.is_active {
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
        .active_override
        .expect("override present when is_active matched")
        .started_at;
    total_minutes(env.now - started_at).is_some_and(|m| m >= f64::from(for_minutes))
}

/// Same shape as `override_active`; a null snapshot simply means DND is off.
pub(super) fn do_not_disturb(p: &ActiveForPayload, env: &Env) -> bool {
    let is_currently_active = env.ctx.active_do_not_disturb.is_some();
    if is_currently_active != p.is_active {
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
        .active_do_not_disturb
        .expect("dnd present when is_active matched")
        .started_at;
    total_minutes(env.now - started_at).is_some_and(|m| m >= f64::from(for_minutes))
}

/// `is_active: false` is true whenever the active mode differs from the
/// configured mode — including when no mode-span is active at all.
pub(super) fn pump_state(p: &PumpStatePayload, env: &Env) -> bool {
    let active_mode = env.ctx.active_pump_state.map(|s| EnumValue::Known(s.mode));

    if !p.is_active {
        return active_mode != Some(p.mode);
    }

    let Some(snapshot) = env.ctx.active_pump_state else {
        return false;
    };
    if EnumValue::Known(snapshot.mode) != p.mode {
        return false;
    }
    let Some(for_minutes) = p.for_minutes else {
        return true;
    };
    total_minutes(env.now - snapshot.started_at).is_some_and(|m| m >= f64::from(for_minutes))
}

/// Generic state-span leaf. The PumpMode category is always false (pump
/// modes are `pump_state`'s). The lookup key is the exact `(category, state)`
/// pair; a null state means "any state of this category".
pub(super) fn state_span_active(p: &StateSpanPayload, env: &Env) -> bool {
    let snapshot = match p.category {
        EnumValue::Known(StateSpanCategory::PumpMode) => return false,
        EnumValue::Known(category) => env.ctx.active_state_spans.get(&(category, p.state.clone())),
        EnumValue::Undefined(_) => None,
    };

    if !p.is_active {
        return snapshot.is_none();
    }
    let Some(snapshot) = snapshot else {
        return false;
    };
    let Some(for_minutes) = p.for_minutes else {
        return true;
    };
    total_minutes(env.now - snapshot.started_at).is_some_and(|m| m >= f64::from(for_minutes))
}

/// Sleep-session leaf. Matches the pre-computed `sleep_session_active` signal
/// against the asserted side: `is_active` true fires while a session is active,
/// false fires while none is.
pub(super) fn sleep_session_active(p: &SleepSessionPayload, env: &Env) -> bool {
    env.ctx.sleep_session_active == p.is_active
}
