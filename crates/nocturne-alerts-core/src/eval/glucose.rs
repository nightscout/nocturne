//! Glucose-fact leaves: threshold, rate_of_change, trend, predicted,
//! glucose_bucket, staleness.

use std::collections::BTreeSet;

use chrono::{DateTime, TimeDelta, Utc};
use rust_decimal::Decimal;

use super::elapsed::{ClockLeaf, Elapsed, Measure, decimal};
use super::signal::{DataStopped, data_stopped};
use super::{Env, Held, MetPoint};
use crate::clock_anomaly::ClockAnchor;
use crate::compare::Unit;
use crate::context::{PredictedPoint, SensorContext};
use crate::enums::{CmpOp, ForecastCurve, SensorState};
use crate::facts::ContextFact;
use crate::model::{
    ConditionKind, CurveSelection, Extreme, GlucoseBucketPayload, Margined, Payload,
    PredictedPayload, RateOfChangePayload, StalenessPayload, ThresholdPayload, TrendPayload,
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

/// Why the sensor's state leaves a judged glucose leaf unknown, whatever
/// value the reading has: a state other than ok, or one this crate cannot
/// name, which may be any of them (engine-semantics.md §3, sensor state).
/// Absent and ok leave the leaf as it is without the fact.
fn sensor_gate(ctx: &SensorContext, now: DateTime<Utc>) -> Option<UnknownReason> {
    match ctx.sensor_state_at(now)?.state {
        Some(SensorState::Ok) => None,
        Some(_) => Some(UnknownReason::SensorNotOk),
        None => Some(UnknownReason::Unrecognised),
    }
}

/// A judged glucose leaf: `test` of the fresh fact, holding since the newest
/// reading's timestamp, the instant the context says it held at
/// (engine-semantics.md §3). A timestamp after `now` is no earlier start than
/// `now`, which the `sustained` clamp reads. A sensor that is not ok leaves
/// it unknown before the reading is read.
fn judged<T>(fact: Option<T>, env: &mut Env, test: impl FnOnce(T) -> bool) -> Held {
    if let Some(reason) = sensor_gate(env.ctx, env.now) {
        return Held::unknown(ContextFact::SensorState, reason);
    }
    match fresh(fact, env) {
        Ok(value) => reading(test(value).into(), env.ctx),
        Err(reason) => Held::unknown(ContextFact::Glucose, reason),
    }
}

/// When a leaf that [`judges_freshness`] can turn unknown with only `now`
/// advancing: `None` for any other kind, and for a leaf whose fact is absent
/// or whose sensor is not ok, which is unknown already.
pub(crate) fn freshness_expiry(
    payload: &Payload,
    ctx: &SensorContext,
    bound: TimeDelta,
    now: DateTime<Utc>,
) -> Option<Elapsed> {
    if sensor_gate(ctx, now).is_some() {
        return None;
    }
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
    compared(p, env.ctx.latest_value, env)
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &mut Env) -> Held {
    compared(p, env.ctx.trend_rate, env)
}

/// A judged glucose leaf's comparison of `fact` with its limit.
fn compared(p: &impl Margined, fact: Option<Decimal>, env: &mut Env) -> Held {
    let (op, _) = p.comparison();
    let limit = env.limit(p);
    judged(fact, env, |actual| op.apply(actual, limit))
}

/// A bucket name the crate cannot name reads as an absent bucket (§4).
pub(super) fn trend(p: &TrendPayload, env: &mut Env) -> Held {
    judged(env.ctx.trend_bucket, env, |bucket| p.bucket == bucket)
}

/// True if any point of the selected series with `offset_minutes <=
/// within_minutes` satisfies the comparison; points beyond the horizon are
/// skipped. A selected series the host did not send is unknown. Not judged:
/// its points are not facts of the reading.
pub(super) fn predicted(p: &PredictedPayload, env: &Env) -> Held {
    predicted_met(p, env).0
}

/// [`predicted`] with the first point, in offset order, that met the
/// comparison (engine-semantics.md §3, `predicted`).
pub(super) fn predicted_met(p: &PredictedPayload, env: &Env) -> (Held, Option<MetPoint>) {
    let limit = env.limit(p);
    let within = p.within_minutes.get();
    let meets = |mgdl: Decimal| p.operator.apply(mgdl, limit);
    let met = match p.selection() {
        CurveSelection::One(curve) => {
            let series = env.ctx.prediction_series(curve);
            if series.is_empty() {
                return (absent_series(), None);
            }
            let mut horizon: Vec<&PredictedPoint> = series
                .iter()
                .filter(|point| point.offset_minutes <= within)
                .collect();
            horizon.sort_by_key(|point| point.offset_minutes);
            horizon
                .into_iter()
                .find(|point| meets(point.mgdl))
                .map(|point| MetPoint::on_curve(curve, point.offset_minutes, point.mgdl))
        }
        CurveSelection::Combined(extreme, curves) => {
            let Some(series) = curves
                .iter()
                .map(|&curve| {
                    let points = env.ctx.prediction_series(curve);
                    (!points.is_empty()).then_some((curve, points))
                })
                .collect::<Option<Vec<_>>>()
            else {
                return (absent_series(), None);
            };
            shared_offsets(&series, within)
                .into_iter()
                .find_map(|offset| {
                    extreme_at(&series, offset, extreme)
                        .filter(|&(_, mgdl)| meets(mgdl))
                        .map(|(curve, mgdl)| MetPoint::on_curve(curve, offset, mgdl))
                })
        }
    };
    (reading(met.is_some().into(), env.ctx), met)
}

fn absent_series() -> Held {
    Held::unknown(ContextFact::Predictions, UnknownReason::Absent)
}

/// The offsets within the horizon at which every series has a point, in
/// order: a combined value exists only where each listed curve does.
fn shared_offsets(series: &[(ForecastCurve, &[PredictedPoint])], within: i32) -> BTreeSet<i32> {
    let offsets = |points: &[PredictedPoint]| -> BTreeSet<i32> {
        points
            .iter()
            .map(|point| point.offset_minutes)
            .filter(|&offset| offset <= within)
            .collect()
    };
    let mut all = series.iter().map(|&(_, points)| offsets(points));
    let first = all.next().unwrap_or_default();
    all.fold(first, |shared, next| &shared & &next)
}

/// The lowest (`Min`) or highest (`Max`) point at `offset` across `series`,
/// with the curve that supplied it: of equal values, the first listed.
fn extreme_at(
    series: &[(ForecastCurve, &[PredictedPoint])],
    offset: i32,
    extreme: Extreme,
) -> Option<(ForecastCurve, Decimal)> {
    series
        .iter()
        .flat_map(|&(curve, points)| {
            points
                .iter()
                .filter(move |point| point.offset_minutes == offset)
                .map(move |point| (curve, point.mgdl))
        })
        .fold(None, |best, (curve, mgdl)| match best {
            Some((_, held))
                if match extreme {
                    Extreme::Min => mgdl >= held,
                    Extreme::Max => mgdl <= held,
                } =>
            {
                best
            }
            _ => Some((curve, mgdl)),
        })
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
