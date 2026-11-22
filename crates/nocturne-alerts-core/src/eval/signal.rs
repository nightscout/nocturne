//! `signal_loss` leaf (engine-semantics.md §5).

use chrono::{DateTime, Duration, Utc};

use super::elapsed::{ClockLeaf, Elapsed, Measure};
use crate::clock_anomaly::ClockAnchor;
use crate::context::SensorContext;
use crate::facts::ContextFact;
use crate::model::SignalLossPayload;
use crate::truth::Truth;
use crate::unknown::UnknownReason;

/// What `signal_loss` and `staleness` measure the time since data stopped
/// from (engine-semantics.md §3).
pub(super) enum DataStopped {
    At(DateTime<Utc>, ClockAnchor),
    /// A reading exists but its time does not: infinitely stale.
    Forever,
    Unknown,
}

/// The newest reading; with no reading history at all, the instant the rule
/// was enabled.
pub(super) fn data_stopped(ctx: &SensorContext, enabled_at: Option<DateTime<Utc>>) -> DataStopped {
    match (ctx.last_reading_at, ctx.latest_timestamp) {
        (Some(at), _) => DataStopped::At(at, ClockAnchor::LastReadingAt),
        (None, Some(_)) => DataStopped::Forever,
        (None, None) => enabled_at.map_or(DataStopped::Unknown, |at| {
            DataStopped::At(at, ClockAnchor::EnabledAt)
        }),
    }
}

/// True once `timeout_minutes` have passed since data stopped, compared as
/// exact durations.
pub(super) fn signal_loss(
    p: &SignalLossPayload,
    ctx: &SensorContext,
    enabled_at: Option<DateTime<Utc>>,
) -> ClockLeaf {
    match data_stopped(ctx, enabled_at) {
        DataStopped::At(anchor, source) => ClockLeaf::Measured(Elapsed {
            anchor,
            source: Some(source),
            measure: Measure::AtLeast(Duration::minutes(i64::from(p.timeout_minutes.get()))),
        }),
        DataStopped::Forever => ClockLeaf::Fixed(Truth::True),
        DataStopped::Unknown => ClockLeaf::Unknown(ContextFact::Glucose, UnknownReason::Absent),
    }
}
