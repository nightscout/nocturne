//! Glucose-fact leaves: threshold, rate_of_change, trend, predicted,
//! glucose_bucket, staleness.

use rust_decimal::Decimal;

use super::Env;
use crate::compare::{compare, decimal_from_f64_cs, total_minutes};
use crate::model::{
    ComparePayload, GlucoseBucketPayload, PredictedPayload, RateOfChangePayload, StalenessPayload,
    TREND_BUCKET_NAMES, ThresholdPayload, TrendPayload,
};

/// Strict comparison: below `v < value`, above `v > value`; unknown direction
/// or no reading is false.
pub(super) fn threshold(p: &ThresholdPayload, env: &Env) -> bool {
    let Some(latest) = env.ctx.latest_value else {
        return false;
    };
    let Some(direction) = p.direction.as_deref() else {
        return false;
    };
    match direction.to_lowercase().as_str() {
        "below" => latest < p.value,
        "above" => latest > p.value,
        _ => false,
    }
}

/// Inclusive comparison: falling `rate' <= -rate`, rising `rate' >= rate`.
pub(super) fn rate_of_change(p: &RateOfChangePayload, env: &Env) -> bool {
    let Some(rate) = env.ctx.trend_rate else {
        return false;
    };
    let Some(direction) = p.direction.as_deref() else {
        return false;
    };
    match direction.to_lowercase().as_str() {
        "falling" => rate <= -p.rate,
        "rising" => rate >= p.rate,
        _ => false,
    }
}

/// Case-insensitive equality of trend-bucket wire forms.
pub(super) fn trend(p: &TrendPayload, env: &Env) -> bool {
    let Some(bucket) = env.ctx.trend_bucket else {
        return false;
    };
    let Some(configured) = p.bucket.as_deref() else {
        return false;
    };
    if configured.is_empty() {
        return false;
    }
    let actual = TREND_BUCKET_NAMES[bucket as usize];
    actual.eq_ignore_ascii_case(configured)
}

/// True if any prediction with `OffsetMinutes <= within_minutes` satisfies the
/// comparison; points beyond the horizon are skipped, not range-checked below.
pub(super) fn predicted(p: &PredictedPayload, env: &Env) -> bool {
    for point in &env.ctx.predictions {
        if point.offset_minutes > p.within_minutes {
            continue;
        }
        if compare(point.mgdl, p.operator.as_deref(), p.value) {
            return true;
        }
    }
    false
}

/// Set membership over the precomputed context bucket.
pub(super) fn glucose_bucket(p: &GlucoseBucketPayload, env: &Env) -> bool {
    let Some(bucket) = env.ctx.glucose_bucket else {
        return false;
    };
    let Some(buckets) = &p.buckets else {
        return false;
    };
    if buckets.is_empty() {
        return false;
    }
    buckets.contains(&bucket)
}

/// Minutes since the last reading vs a threshold. Cold start (both
/// `last_reading_at` and `latest_timestamp` null) is false, taking precedence
/// over the infinity convention; `last_reading_at` alone null means elapsed is
/// +infinity (`>`/`>=` true, others false). Elapsed is computed in f64 then
/// cast to decimal the way C# casts.
pub(super) fn staleness(p: &StalenessPayload, env: &Env) -> bool {
    if env.ctx.last_reading_at.is_none() && env.ctx.latest_timestamp.is_none() {
        return false;
    }
    let Some(last_reading_at) = env.ctx.last_reading_at else {
        return matches!(p.operator.as_deref(), Some(">") | Some(">="));
    };
    let elapsed = total_minutes(env.now - last_reading_at);
    let Some(elapsed) = decimal_from_f64_cs(elapsed) else {
        return false;
    };
    compare(elapsed, p.operator.as_deref(), Decimal::from(p.value))
}

/// Shared `{operator, value}` comparison against an optional context decimal.
pub(super) fn compare_optional(p: &ComparePayload, actual: Option<Decimal>) -> bool {
    match actual {
        Some(actual) => compare(actual, p.operator.as_deref(), p.value),
        None => false,
    }
}
