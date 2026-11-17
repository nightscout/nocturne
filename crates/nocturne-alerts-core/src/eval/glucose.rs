//! Glucose-fact leaves: threshold, rate_of_change, trend, predicted,
//! glucose_bucket, staleness.

use super::Env;
use crate::compare::Unit;
use crate::enums::{CmpOp, RateDirection, ThresholdDirection, holds};
use crate::model::{
    GlucoseBucketPayload, PredictedPayload, RateOfChangePayload, StalenessPayload,
    ThresholdPayload, TrendPayload,
};

/// Strict comparison: below `v < value`, above `v > value`; no reading is
/// false.
pub(super) fn threshold(p: &ThresholdPayload, env: &Env) -> bool {
    env.ctx
        .latest_value
        .is_some_and(|latest| match p.direction {
            ThresholdDirection::Below => latest < p.value,
            ThresholdDirection::Above => latest > p.value,
        })
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &Env) -> bool {
    env.ctx.trend_rate.is_some_and(|rate| match p.direction {
        RateDirection::Falling => rate <= -p.rate,
        RateDirection::Rising => rate >= p.rate,
    })
}

pub(super) fn trend(p: &TrendPayload, env: &Env) -> bool {
    env.ctx
        .trend_bucket
        .is_some_and(|bucket| p.bucket == bucket)
}

/// True if any prediction with `offset_minutes <= within_minutes` satisfies the
/// comparison; points beyond the horizon are skipped.
pub(super) fn predicted(p: &PredictedPayload, env: &Env) -> bool {
    env.ctx
        .predictions
        .iter()
        .filter(|point| point.offset_minutes <= p.within_minutes.get())
        .any(|point| holds(p.operator, Some(point.mgdl), p.value))
}

/// Set membership over the precomputed context bucket.
pub(super) fn glucose_bucket(p: &GlucoseBucketPayload, env: &Env) -> bool {
    env.ctx
        .glucose_bucket
        .is_some_and(|bucket| p.buckets.contains(&bucket))
}

/// Minutes since the last reading vs a threshold. Cold start (both
/// `last_reading_at` and `latest_timestamp` null) is false, taking precedence
/// over the infinity convention; `last_reading_at` alone null means elapsed is
/// +infinity (`>`/`>=` true, others false).
pub(super) fn staleness(p: &StalenessPayload, env: &Env) -> bool {
    if env.ctx.last_reading_at.is_none() && env.ctx.latest_timestamp.is_none() {
        return false;
    }
    let Some(last_reading_at) = env.ctx.last_reading_at else {
        return matches!(p.operator, CmpOp::Gt | CmpOp::Ge);
    };
    env.compare_elapsed(last_reading_at, Unit::Minutes, p.operator, p.value.get())
}
