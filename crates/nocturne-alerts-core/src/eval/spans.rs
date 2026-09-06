//! State-span and cross-alert leaves: alert_state, override_active,
//! do_not_disturb, pump_state, state_span_active, sleep_session_active.

use super::Env;
use crate::compare::total_minutes;
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
    let Some(state) = p.state.as_deref() else {
        return false;
    };
    let state_lower = state.to_lowercase();
    let state_matches = match state_lower.as_str() {
        "firing" => snapshot.state.eq_ignore_ascii_case("firing"),
        "unacknowledged" => {
            snapshot.state.eq_ignore_ascii_case("firing") && snapshot.acknowledged_at.is_none()
        }
        "acknowledged" => snapshot.acknowledged_at.is_some(),
        _ => false,
    };
    if !state_matches {
        return false;
    }
    let Some(for_minutes) = p.for_minutes else {
        return true;
    };
    let anchor = if state_lower == "acknowledged" {
        snapshot
            .acknowledged_at
            .expect("acknowledged match implies acknowledged_at")
    } else {
        snapshot.triggered_at
    };
    total_minutes(env.now - anchor) >= f64::from(for_minutes)
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
    total_minutes(env.now - started_at) >= f64::from(for_minutes)
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
    total_minutes(env.now - started_at) >= f64::from(for_minutes)
}

/// `is_active: false` is true whenever the active mode differs from the
/// configured mode — including when no mode-span is active at all.
pub(super) fn pump_state(p: &PumpStatePayload, env: &Env) -> bool {
    let active_mode = env.ctx.active_pump_state.map(|s| s.mode);

    if !p.is_active {
        return active_mode != Some(p.mode);
    }

    let Some(snapshot) = env.ctx.active_pump_state else {
        return false;
    };
    if snapshot.mode != p.mode {
        return false;
    }
    let Some(for_minutes) = p.for_minutes else {
        return true;
    };
    total_minutes(env.now - snapshot.started_at) >= f64::from(for_minutes)
}

/// Generic state-span leaf. The PumpMode category (ordinal 0) is always false
/// (defence in depth). The lookup key is the exact `(category, state)` pair —
/// a null state means "any state of this category" because the enricher loads
/// that key shape.
pub(super) fn state_span_active(p: &StateSpanPayload, env: &Env) -> bool {
    if p.category == 0 {
        return false;
    }
    let key = (p.category, p.state.clone());
    let snapshot = env.ctx.active_state_spans.get(&key);

    if !p.is_active {
        return snapshot.is_none();
    }
    let Some(snapshot) = snapshot else {
        return false;
    };
    let Some(for_minutes) = p.for_minutes else {
        return true;
    };
    total_minutes(env.now - snapshot.started_at) >= f64::from(for_minutes)
}

/// Sleep-session leaf. Matches the pre-computed `sleep_session_active` signal
/// against the asserted side: `is_active` true fires while a session is active,
/// false fires while none is.
pub(super) fn sleep_session_active(p: &SleepSessionPayload, env: &Env) -> bool {
    env.ctx.sleep_session_active == p.is_active
}
