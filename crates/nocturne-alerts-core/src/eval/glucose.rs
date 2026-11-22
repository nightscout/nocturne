//! Glucose-fact leaves: threshold, rate_of_change, trend, predicted,
//! glucose_bucket, staleness.

use chrono::{DateTime, TimeDelta, Utc};

use super::elapsed::{ClockLeaf, Elapsed, Measure, decimal};
use super::signal::{DataStopped, data_stopped};
use super::{Env, Held};
use crate::clock_anomaly::ClockAnchor;
use crate::compare::Unit;
use crate::context::SensorContext;
use crate::enums::{CmpOp, RateDirection, ThresholdDirection, holds};
use crate::facts::ContextFact;
use crate::model::{
    ConditionKind, GlucoseBucketPayload, Payload, PredictedPayload, RateOfChangePayload,
    StalenessPayload, ThresholdPayload, TrendPayload,
};
use crate::truth::Truth;
use crate::unknown::UnknownReason;

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
/// fresh; without either it never does. The arrival is the host's own
/// instant, and one after `now` is fresh (§3); a timestamp is the device's.
fn expiry(ctx: &SensorContext, bound: TimeDelta) -> Option<Elapsed> {
    let (anchor, source) = match (ctx.latest_received_at, ctx.latest_timestamp) {
        (Some(arrival), _) => (arrival, None),
        (None, Some(timestamp)) => (timestamp, Some(ClockAnchor::LatestTimestamp)),
        (None, None) => return None,
    };
    Some(Elapsed {
        anchor,
        source,
        measure: Measure::Over(bound),
    })
}

/// `fact` while the reading is known to be fresh at `env.now`, else why it is
/// not read: absent, stale, or of an age that a timestamp ahead of the clock
/// leaves unknown.
fn fresh<T>(fact: Option<T>, env: &mut Env) -> Result<T, UnknownReason> {
    let stale = expiry(env.ctx, env.glucose_freshness).map_or(Truth::False, |e| env.measure(e));
    let fact = fact.ok_or(UnknownReason::Absent)?;
    match stale {
        Truth::False => Ok(fact),
        Truth::True => Err(UnknownReason::Stale),
        Truth::Unknown => Err(UnknownReason::AheadOfClock),
    }
}

/// A judged glucose leaf: `test` of the fresh fact, holding since the newest
/// reading's timestamp, the instant the context says it held at
/// (engine-semantics.md §3). A timestamp after `now` is no earlier start than
/// `now`, which the `sustained` clamp reads.
fn judged<T>(fact: Option<T>, env: &mut Env, test: impl FnOnce(T) -> bool) -> Held {
    match fresh(fact, env) {
        Ok(value) => reading(test(value).into(), env.ctx),
        Err(reason) => Held::unknown(ContextFact::Glucose, reason),
    }
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

/// A glucose leaf's truth with the newest reading's timestamp.
fn reading(truth: Truth, ctx: &SensorContext) -> Held {
    Held::since(truth, ctx.latest_timestamp)
}

/// Strict comparison: below `v < value`, above `v > value`; no fresh reading
/// is unknown.
pub(super) fn threshold(p: &ThresholdPayload, env: &mut Env) -> Held {
    judged(env.ctx.latest_value, env, |latest| match p.direction {
        ThresholdDirection::Below => latest < p.value,
        ThresholdDirection::Above => latest > p.value,
    })
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &mut Env) -> Held {
    judged(env.ctx.trend_rate, env, |rate| match p.direction {
        RateDirection::Falling => rate <= -p.rate,
        RateDirection::Rising => rate >= p.rate,
    })
}

/// A bucket name the crate cannot name reads as an absent bucket (§4).
pub(super) fn trend(p: &TrendPayload, env: &mut Env) -> Held {
    judged(env.ctx.trend_bucket, env, |bucket| p.bucket == bucket)
}

/// True if any prediction with `offset_minutes <= within_minutes` satisfies the
/// comparison; points beyond the horizon are skipped. No predictions is
/// unknown. Not judged: its points are not facts of the reading.
pub(super) fn predicted(p: &PredictedPayload, env: &Env) -> Held {
    if env.ctx.predictions.is_empty() {
        return Held::unknown(ContextFact::Predictions, UnknownReason::Absent);
    }
    reading(
        Truth::any(
            env.ctx
                .predictions
                .iter()
                .filter(|point| point.offset_minutes <= p.within_minutes.get())
                .map(|point| holds(p.operator, Some(point.mgdl), p.value)),
        ),
        env.ctx,
    )
}

/// Set membership over the precomputed context bucket.
pub(super) fn glucose_bucket(p: &GlucoseBucketPayload, env: &mut Env) -> Held {
    judged(env.ctx.glucose_bucket, env, |bucket| {
        p.buckets.contains(&bucket)
    })
}

/// Minutes since data stopped vs a threshold; infinitely stale is `>`/`>=`
/// true, others false.
pub(super) fn staleness(
    p: &StalenessPayload,
    ctx: &SensorContext,
    enabled_at: Option<DateTime<Utc>>,
) -> ClockLeaf {
    match data_stopped(ctx, enabled_at) {
        DataStopped::At(anchor, source) => decimal(
            anchor,
            Some(source),
            Unit::Minutes,
            p.operator,
            p.value.get(),
        ),
        DataStopped::Forever => {
            ClockLeaf::Fixed(matches!(p.operator, CmpOp::Gt | CmpOp::Ge).into())
        }
        DataStopped::Unknown => ClockLeaf::Unknown(ContextFact::Glucose, UnknownReason::Absent),
    }
}
