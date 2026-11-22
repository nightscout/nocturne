//! The loop's newest determination: `eventual_glucose` and `carbs_required`
//! (engine-semantics.md §3).

use chrono::TimeDelta;
use rust_decimal::Decimal;

use super::elapsed::{Elapsed, Measure};
use super::{Env, Held, MetPoint};
use crate::clock_anomaly::ClockAnchor;
use crate::context::{ApsDetermination, SensorContext};
use crate::facts::ContextFact;
use crate::model::{ComparePayload, Margined, Payload};
use crate::truth::Truth;
use crate::unknown::UnknownReason;

type DeterminationValue = fn(&ApsDetermination) -> Option<Decimal>;

/// The value a determination leaf compares.
fn value_of(payload: &Payload) -> Option<(&ComparePayload, DeterminationValue)> {
    match payload {
        Payload::EventualGlucose(p) => Some((p, |d| d.eventual_mgdl)),
        Payload::CarbsRequired(p) => Some((p, |d| d.carbs_required_grams)),
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

fn unknown(reason: UnknownReason) -> (Held, Option<MetPoint>) {
    (Held::unknown(ContextFact::ApsDetermination, reason), None)
}

/// A determination leaf's truth and, when it is true, the value that met its
/// comparison. No determination, or one without the value, is absent; one
/// older than the rule's freshness bound is stale.
pub(super) fn determined(payload: &Payload, env: &mut Env) -> (Held, Option<MetPoint>) {
    let Some((p, value)) = value_of(payload) else {
        return (Truth::False.into(), None);
    };
    let Some(d) = env.ctx.aps_determination else {
        return unknown(UnknownReason::Absent);
    };
    let stale = env.measure(expiry(&d, env.glucose_freshness));
    let Some(actual) = value(&d) else {
        return unknown(UnknownReason::Absent);
    };
    match stale {
        Truth::True => unknown(UnknownReason::Stale),
        Truth::Unknown => unknown(UnknownReason::AheadOfClock),
        Truth::False => {
            let (op, _) = p.comparison();
            let met = op
                .apply(actual, env.limit(p))
                .then_some(MetPoint::value(actual));
            (Truth::from(met.is_some()).into(), met)
        }
    }
}

/// When a determination leaf whose value is present turns stale with only
/// `now` advancing; `None` for any other leaf.
pub(crate) fn determination_expiry(
    payload: &Payload,
    ctx: &SensorContext,
    bound: TimeDelta,
) -> Option<Elapsed> {
    let (_, value) = value_of(payload)?;
    let d = ctx.aps_determination?;
    value(&d).map(|_| expiry(&d, bound))
}
