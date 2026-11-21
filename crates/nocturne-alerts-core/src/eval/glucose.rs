//! Glucose-fact leaves: threshold, rate_of_change, trend, predicted,
//! glucose_bucket, staleness.

use chrono::{DateTime, TimeDelta, Utc};

use super::Env;
use super::elapsed::{ClockLeaf, Elapsed, Measure, decimal};
use super::signal::{DataStopped, data_stopped};
use crate::compare::Unit;
use crate::context::SensorContext;
use crate::enums::{CmpOp, RateDirection, ThresholdDirection, holds};
use crate::model::{
    ConditionKind, GlucoseBucketPayload, Payload, PredictedPayload, RateOfChangePayload,
    StalenessPayload, ThresholdPayload, TrendPayload,
};
use crate::truth::Truth;

/// How old a reading may be before its glucose facts are unknown, for a rule
/// without its own bound (engine-semantics.md §3).
pub const DEFAULT_GLUCOSE_FRESHNESS: TimeDelta = TimeDelta::minutes(11);

/// The most minutes a rule's own bound may be: a day. A longer one would judge a reading
/// however old, which the bound exists to stop.
pub const MAX_GLUCOSE_FRESHNESS_MINUTES: i64 = 1440;

/// Whether `kind` reads its glucose fact only while the reading is fresh.
/// `staleness` and `signal_loss` say that data stopped, so they are not.
#[must_use]
pub(crate) fn judges_freshness(kind: ConditionKind) -> bool {
    matches!(
        kind,
        ConditionKind::Threshold
            | ConditionKind::RateOfChange
            | ConditionKind::Trend
            | ConditionKind::GlucoseBucket
    )
}

/// The reading goes stale once more than `bound` has passed since the host
/// received it, else since its timestamp, so a reading exactly `bound` old is
/// fresh; without either it never does.
fn expiry(ctx: &SensorContext, bound: TimeDelta) -> Option<Elapsed> {
    ctx.latest_received_at
        .or(ctx.latest_timestamp)
        .map(|anchor| Elapsed {
            anchor,
            measure: Measure::Over(bound),
        })
}

/// `fact` while the reading is fresh at `env.now`, else absent.
fn fresh<T>(fact: Option<T>, env: &Env) -> Option<T> {
    let stale = expiry(env.ctx, env.glucose_freshness).is_some_and(|e| e.holds(env.now));
    fact.filter(|_| !stale)
}

/// When a leaf that [`judges_freshness`] can turn unknown with only `now`
/// advancing: `None` for any other kind, and for a leaf whose fact is absent,
/// which is unknown already.
pub(crate) fn freshness_expiry(
    payload: &Payload,
    ctx: &SensorContext,
    bound: TimeDelta,
) -> Option<Elapsed> {
    let present = match payload {
        Payload::Threshold(_) => ctx.latest_value.is_some(),
        Payload::RateOfChange(_) => ctx.trend_rate.is_some(),
        Payload::Trend(_) => ctx.trend_bucket.is_some(),
        Payload::GlucoseBucket(_) => ctx.glucose_bucket.is_some(),
        _ => false,
    };
    present.then(|| expiry(ctx, bound)).flatten()
}

/// Strict comparison: below `v < value`, above `v > value`; no fresh reading
/// is unknown.
pub(super) fn threshold(p: &ThresholdPayload, env: &Env) -> Truth {
    Truth::of(
        fresh(env.ctx.latest_value, env).map(|latest| match p.direction {
            ThresholdDirection::Below => latest < p.value,
            ThresholdDirection::Above => latest > p.value,
        }),
    )
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &Env) -> Truth {
    Truth::of(
        fresh(env.ctx.trend_rate, env).map(|rate| match p.direction {
            RateDirection::Falling => rate <= -p.rate,
            RateDirection::Rising => rate >= p.rate,
        }),
    )
}

pub(super) fn trend(p: &TrendPayload, env: &Env) -> Truth {
    Truth::of(fresh(env.ctx.trend_bucket, env).map(|bucket| p.bucket == bucket))
}

/// True if any prediction with `offset_minutes <= within_minutes` satisfies the
/// comparison; points beyond the horizon are skipped. No predictions is
/// unknown.
pub(super) fn predicted(p: &PredictedPayload, env: &Env) -> Truth {
    if env.ctx.predictions.is_empty() {
        return Truth::Unknown;
    }
    Truth::any(
        env.ctx
            .predictions
            .iter()
            .filter(|point| point.offset_minutes <= p.within_minutes.get())
            .map(|point| holds(p.operator, Some(point.mgdl), p.value)),
    )
}

/// Set membership over the precomputed context bucket.
pub(super) fn glucose_bucket(p: &GlucoseBucketPayload, env: &Env) -> Truth {
    Truth::of(fresh(env.ctx.glucose_bucket, env).map(|bucket| p.buckets.contains(&bucket)))
}

/// Minutes since data stopped vs a threshold; infinitely stale is `>`/`>=`
/// true, others false.
pub(super) fn staleness(
    p: &StalenessPayload,
    ctx: &SensorContext,
    enabled_at: Option<DateTime<Utc>>,
) -> ClockLeaf {
    match data_stopped(ctx, enabled_at) {
        DataStopped::At(anchor) => decimal(anchor, Unit::Minutes, p.operator, p.value.get()),
        DataStopped::Forever => {
            ClockLeaf::Fixed(matches!(p.operator, CmpOp::Gt | CmpOp::Ge).into())
        }
        DataStopped::Unknown => ClockLeaf::Fixed(Truth::Unknown),
    }
}
