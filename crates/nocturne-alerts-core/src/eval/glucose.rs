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
use crate::enums::{CmpOp, ForecastCurve, LimitFact, SensorState};
use crate::facts::ContextFact;
use crate::model::{
    Bound, ConditionKind, CurveSelection, Extreme, GlucoseBucketPayload, LimitRef, Margined,
    Payload, PredictedPayload, RateOfChangePayload, StalenessPayload, ThresholdPayload,
    TrendPayload,
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

/// A judged glucose fact: the fresh value, or the unknown it leaves. A
/// sensor that is not ok leaves it unknown before the reading is read.
fn judged_value<T>(fact: Option<T>, env: &mut Env) -> Result<T, Held> {
    if let Some(reason) = sensor_gate(env.ctx, env.now) {
        return Err(Held::unknown(ContextFact::SensorState, reason));
    }
    fresh(fact, env).map_err(|reason| Held::unknown(ContextFact::Glucose, reason))
}

/// A judged glucose leaf: `test` of the fresh fact, holding since the newest
/// reading's timestamp, the instant the context says it held at
/// (engine-semantics.md §3). A timestamp after `now` is no earlier start than
/// `now`, which the `sustained` clamp reads.
fn judged<T>(fact: Option<T>, env: &mut Env, test: impl FnOnce(T) -> bool) -> Held {
    match judged_value(fact, env) {
        Ok(value) => reading(test(value).into(), env.ctx),
        Err(unknown) => unknown,
    }
}

/// The limit a comparison leaf compares against in `env`, and the fact it
/// came from (engine-semantics.md §3, a limit from a fact).
#[derive(Debug, Clone, Copy)]
pub(crate) struct Effective {
    pub(crate) value: Decimal,
    pub(crate) fact: Option<LimitFact>,
    /// The rule's offset to the fact; zero for a literal.
    pub(crate) offset: Decimal,
}

impl Effective {
    /// The instant a leaf compared against this limit has held since, given
    /// the one its reading gives: none against a target, which can move at a
    /// schedule boundary after the reading.
    fn since(self, reading: Option<DateTime<Utc>>) -> Option<DateTime<Utc>> {
        match self.fact {
            Some(LimitFact::TargetLow | LimitFact::TargetHigh) => None,
            Some(LimitFact::LatestGlucose) | None => reading,
        }
    }
}

/// The value a `value_from` names plus its offset. `latest_glucose` is read
/// as `threshold` reads its reading; a target the host did not send is
/// absent, never a default. A sum past the decimal range is unknown by the
/// fact, `absent`.
fn referenced(r: &LimitRef, env: &mut Env) -> Result<Decimal, Held> {
    let (fact, value) = match r.fact {
        LimitFact::LatestGlucose => (
            ContextFact::Glucose,
            judged_value(env.ctx.latest_value, env)?,
        ),
        LimitFact::TargetLow => (ContextFact::TargetRange, target(env.ctx.target_low)?),
        LimitFact::TargetHigh => (ContextFact::TargetRange, target(env.ctx.target_high)?),
    };
    value
        .checked_add(r.offset())
        .ok_or_else(|| Held::unknown(fact, UnknownReason::Absent))
}

fn target(value: Option<Decimal>) -> Result<Decimal, Held> {
    value.ok_or_else(|| Held::unknown(ContextFact::TargetRange, UnknownReason::Absent))
}

/// `p`'s effective limit (a literal, or a fact plus its offset), moved by
/// its margin while the excursion is open, or the unknown that leaves it.
pub(crate) fn effective_limit(p: &impl Margined, env: &mut Env) -> Result<Effective, Held> {
    let (base, fact, offset) = match p.bound() {
        Bound::Literal(value) => (value, None, Decimal::ZERO),
        Bound::Fact(r) => (referenced(r, env)?, Some(r.fact), r.offset()),
    };
    Ok(Effective {
        value: p.widened(base, env.excursion_open, env.negated),
        fact,
        offset,
    })
}

/// Both sides of a comparison, or the unknown of every side that is unknown.
pub(crate) fn both<A, B>(left: Result<A, Held>, right: Result<B, Held>) -> Result<(A, B), Held> {
    match (left, right) {
        (Ok(a), Ok(b)) => Ok((a, b)),
        (Err(a), Err(b)) => Err(Held::from(Truth::Unknown).because(a.unknown.union(b.unknown))),
        (Err(unknown), Ok(_)) | (Ok(_), Err(unknown)) => Err(unknown),
    }
}

/// When a leaf that [`judges_freshness`], or whose limit names
/// `latest_glucose`, can turn unknown with only `now` advancing: `None` for
/// any other leaf, and for a leaf whose fact is absent or whose sensor is
/// not ok, which is unknown already.
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
    } || (payload.limit_fact() == Some(LimitFact::LatestGlucose)
        && ctx.latest_value.is_some());
    present.then(|| expiry(ctx, bound)).flatten()
}

/// A glucose leaf's truth with the newest reading's timestamp.
fn reading(truth: Truth, ctx: &SensorContext) -> Held {
    Held::since(truth, ctx.latest_timestamp)
}

/// Strict comparison: below `v < limit`, above `v > limit`; no fresh reading
/// is unknown.
pub(super) fn threshold(p: &ThresholdPayload, env: &mut Env) -> Held {
    threshold_met(p, env).0
}

/// [`threshold`] with, when it is true against a limit from a fact, the
/// reading and that limit.
pub(super) fn threshold_met(p: &ThresholdPayload, env: &mut Env) -> (Held, Option<MetPoint>) {
    match compared(p, env.ctx.latest_value, env) {
        Ok((held, actual, limit)) => {
            let met = (held.truth.is_true() && limit.fact.is_some())
                .then(|| MetPoint::value(actual).against(limit));
            (held, met)
        }
        Err(unknown) => (unknown, None),
    }
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &mut Env) -> Held {
    compared(p, env.ctx.trend_rate, env).map_or_else(|unknown| unknown, |(held, _, _)| held)
}

/// A judged glucose leaf's comparison of `fact` with its limit: unknown when
/// either side is.
fn compared(
    p: &impl Margined,
    fact: Option<Decimal>,
    env: &mut Env,
) -> Result<(Held, Decimal, Effective), Held> {
    let actual = judged_value(fact, env);
    let (actual, limit) = both(actual, effective_limit(p, env))?;
    let truth = Truth::from(p.operator().apply(actual, limit.value));
    Ok((
        Held::since(truth, limit.since(env.ctx.latest_timestamp)),
        actual,
        limit,
    ))
}

/// A bucket name the crate cannot name reads as an absent bucket (§4).
pub(super) fn trend(p: &TrendPayload, env: &mut Env) -> Held {
    judged(env.ctx.trend_bucket, env, |bucket| p.bucket == bucket)
}

/// True if any point of the selected series with `offset_minutes <=
/// within_minutes` satisfies the comparison; points beyond the horizon are
/// skipped. A selected series the host did not send is unknown. Not judged:
/// its points are not facts of the reading.
pub(super) fn predicted(p: &PredictedPayload, env: &mut Env) -> Held {
    predicted_met(p, env).0
}

/// [`predicted`] with the first point, in offset order, that met the
/// comparison (engine-semantics.md §3, `predicted`).
pub(super) fn predicted_met(p: &PredictedPayload, env: &mut Env) -> (Held, Option<MetPoint>) {
    let series = if series_present(p, env.ctx) {
        Ok(())
    } else {
        Err(absent_series())
    };
    let limit = match both(series, effective_limit(p, env)) {
        Ok(((), limit)) => limit,
        Err(unknown) => return (unknown, None),
    };
    let met = first_met(p, env.ctx, limit.value).map(|point| point.against(limit));
    let truth = Truth::from(met.is_some());
    (
        Held::since(truth, limit.since(env.ctx.latest_timestamp)),
        met,
    )
}

/// Whether the host sent every series `p` selects, with at least one point.
fn series_present(p: &PredictedPayload, ctx: &SensorContext) -> bool {
    match p.selection() {
        CurveSelection::One(curve) => !ctx.prediction_series(curve).is_empty(),
        CurveSelection::Combined(_, curves) => curves
            .iter()
            .all(|&curve| !ctx.prediction_series(curve).is_empty()),
    }
}

/// The first point within the horizon, in offset order, of the selected
/// series that meets the comparison against `limit`.
fn first_met(p: &PredictedPayload, ctx: &SensorContext, limit: Decimal) -> Option<MetPoint> {
    let within = p.within_minutes.get();
    let meets = |mgdl: Decimal| p.operator.apply(mgdl, limit);
    match p.selection() {
        CurveSelection::One(curve) => {
            let mut horizon: Vec<&PredictedPoint> = ctx
                .prediction_series(curve)
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
            let series: Vec<(ForecastCurve, &[PredictedPoint])> = curves
                .iter()
                .map(|&curve| (curve, ctx.prediction_series(curve)))
                .collect();
            shared_offsets(&series, within)
                .into_iter()
                .find_map(|offset| {
                    extreme_at(&series, offset, extreme)
                        .filter(|&(_, mgdl)| meets(mgdl))
                        .map(|(curve, mgdl)| MetPoint::on_curve(curve, offset, mgdl))
                })
        }
    }
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
