//! `signal_loss` leaf (engine-semantics.md §5).

use chrono::Duration;

use super::Env;
use crate::model::SignalLossPayload;

/// True once `now - last_reading_at >= timeout_minutes`, compared as exact
/// durations. No reading history is false; `last_reading_at` absent with a
/// `latest_timestamp` is infinitely stale; a timeout of zero or less is false.
pub(super) fn signal_loss(p: &SignalLossPayload, env: &Env) -> bool {
    if p.timeout_minutes <= 0 {
        return false;
    }
    let Some(last_reading_at) = env.ctx.last_reading_at else {
        return env.ctx.latest_timestamp.is_some();
    };
    env.now.signed_duration_since(last_reading_at)
        >= Duration::minutes(i64::from(p.timeout_minutes))
}
