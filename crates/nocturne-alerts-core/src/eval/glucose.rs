//! Glucose-fact leaves: threshold, rate_of_change, trend, predicted,
//! glucose_bucket, staleness.

use super::Env;
use crate::compare::Unit;
use crate::enums::{CmpOp, EnumValue, RateDirection, ThresholdDirection, holds};
use crate::model::{
    GlucoseBucketPayload, PredictedPayload, RateOfChangePayload, StalenessPayload,
    ThresholdPayload, TrendPayload,
};

/// Strict comparison: below `v < value`, above `v > value`; unknown direction
/// or no reading is false.
pub(super) fn threshold(p: &ThresholdPayload, env: &Env) -> bool {
    let Some(latest) = env.ctx.latest_value else {
        return false;
    };
    match p.direction.value {
        Some(ThresholdDirection::Below) => latest < p.value,
        Some(ThresholdDirection::Above) => latest > p.value,
        None => false,
    }
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &Env) -> bool {
    let Some(rate) = env.ctx.trend_rate else {
        return false;
    };
    match p.direction.value {
        Some(RateDirection::Falling) => rate <= -p.rate,
        Some(RateDirection::Rising) => rate >= p.rate,
        None => false,
    }
}

pub(super) fn trend(p: &TrendPayload, env: &Env) -> bool {
    env.ctx
        .trend_bucket
        .is_some_and(|bucket| p.bucket.value == Some(bucket))
}

/// True if any prediction with `offset_minutes <= within_minutes` satisfies the
/// comparison; points beyond the horizon are skipped.
pub(super) fn predicted(p: &PredictedPayload, env: &Env) -> bool {
    env.ctx
        .predictions
        .iter()
        .filter(|point| point.offset_minutes <= p.within_minutes)
        .any(|point| holds(p.operator.value, Some(point.mgdl), p.value))
}

/// Set membership over the precomputed context bucket.
pub(super) fn glucose_bucket(p: &GlucoseBucketPayload, env: &Env) -> bool {
    env.ctx
        .glucose_bucket
        .zip(p.buckets.as_ref())
        .is_some_and(|(bucket, buckets)| buckets.contains(&EnumValue::Known(bucket)))
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
        return matches!(p.operator.value, Some(CmpOp::Gt | CmpOp::Ge));
    };
    env.compare_elapsed(last_reading_at, Unit::Minutes, p.operator.value, p.value)
}
