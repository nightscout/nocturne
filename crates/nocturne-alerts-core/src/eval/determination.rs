//! The loop's newest determination: `eventual_glucose` and `carbs_required`
//! (engine-semantics.md §3).

use chrono::TimeDelta;
use rust_decimal::Decimal;

use super::elapsed::{Elapsed, Measure};
use super::glucose::{both, effective_limit};
use super::{Env, Held, MetPoint};
use crate::clock_anomaly::ClockAnchor;
use crate::context::{ApsDetermination, SensorContext};
use crate::facts::ContextFact;
use crate::model::{Margined, Payload};
use crate::truth::Truth;
use crate::unknown::UnknownReason;

type DeterminationValue = fn(&ApsDetermination) -> Option<Decimal>;

/// The value a determination leaf compares.
fn value_of(payload: &Payload) -> Option<DeterminationValue> {
    match payload {
        Payload::EventualGlucose(_) => Some(|d| d.eventual_mgdl),
        Payload::CarbsRequired(_) => Some(|d| d.carbs_required_grams),
        _ => None,
    }
}

/// The determination goes stale once more than `bound` has passed since it
/// was made, as a reading does (engine-semantics.md §3, glucose freshness).
fn expiry(d: &ApsDetermination, bound: TimeDelta) -> Elapsed {
    Elapsed {
        anchor: d.at,
        source: Some(ClockAnchor::ApsDetermination),
        measure: Measure::Over(bound),
    }
}

fn unknown(reason: UnknownReason) -> Held {
    Held::unknown(ContextFact::ApsDetermination, reason)
}

/// A determination leaf's truth and, when it is true, the value that met its
/// comparison.
pub(super) fn determined(payload: &Payload, env: &mut Env) -> (Held, Option<MetPoint>) {
    match payload {
        Payload::EventualGlucose(p) => compared(p, |d| d.eventual_mgdl, env),
        Payload::CarbsRequired(p) => compared(p, |d| d.carbs_required_grams, env),
        _ => (Truth::False.into(), None),
    }
}

/// The determination's `value` against `p`'s limit: unknown when either
/// side is.
fn compared(
    p: &impl Margined,
    value: DeterminationValue,
    env: &mut Env,
) -> (Held, Option<MetPoint>) {
    let actual = fresh_value(value, env);
    match both(actual, effective_limit(p, env)) {
        Ok((actual, limit)) => {
            let met = p
                .operator()
                .apply(actual, limit.value)
                .then(|| MetPoint::value(actual).against(limit));
            (Truth::from(met.is_some()).into(), met)
        }
        Err(unknown) => (unknown, None),
    }
}

/// The determination's `value` while it is fresh. No determination, or one
/// without the value, is absent; one older than the rule's freshness bound
/// is stale.
fn fresh_value(value: DeterminationValue, env: &mut Env) -> Result<Decimal, Held> {
    let d = env
        .ctx
        .aps_determination
        .ok_or_else(|| unknown(UnknownReason::Absent))?;
    let stale = env.measure(expiry(&d, env.glucose_freshness));
    let actual = value(&d).ok_or_else(|| unknown(UnknownReason::Absent))?;
    match stale {
        Truth::True => Err(unknown(UnknownReason::Stale)),
        Truth::Unknown => Err(unknown(UnknownReason::AheadOfClock)),
        Truth::False => Ok(actual),
    }
}

/// When a determination leaf whose value is present turns stale with only
/// `now` advancing; `None` for any other leaf.
pub(crate) fn determination_expiry(
    payload: &Payload,
    ctx: &SensorContext,
    bound: TimeDelta,
) -> Option<Elapsed> {
    let value = value_of(payload)?;
    let d = ctx.aps_determination?;
    value(&d).map(|_| expiry(&d, bound))
}
