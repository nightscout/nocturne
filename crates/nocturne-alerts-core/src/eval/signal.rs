//! `signal_loss` leaf (engine-semantics.md §5).

use chrono::Duration;

use super::elapsed::{ClockLeaf, Elapsed, Measure};
use crate::context::SensorContext;
use crate::model::SignalLossPayload;

/// True once `now - last_reading_at >= timeout_minutes`, compared as exact
/// durations. No reading history is false; `last_reading_at` absent with a
/// `latest_timestamp` is infinitely stale.
pub(super) fn signal_loss(p: &SignalLossPayload, ctx: &SensorContext) -> ClockLeaf {
    let Some(last_reading_at) = ctx.last_reading_at else {
        return ClockLeaf::Fixed(ctx.latest_timestamp.is_some());
    };
    ClockLeaf::Measured(Elapsed {
        anchor: last_reading_at,
        measure: Measure::AtLeast(Duration::minutes(i64::from(p.timeout_minutes.get()))),
    })
}
