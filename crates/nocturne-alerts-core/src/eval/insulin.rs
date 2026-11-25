//! Insulin/treatment leaves: iob, cob, reservoir, temp_basal.

use rust_decimal::Decimal;

use super::{Env, Held, unknown_by};
use crate::enums::{CmpOp, TempBasalMetric, holds};
use crate::facts::ContextFact;
use crate::model::{ComparePayload, TempBasalPayload};
use crate::truth::Truth;
use crate::unknown::{UnknownFacts, UnknownReason};

pub(super) fn iob(p: &ComparePayload, env: &Env) -> Held {
    unknown_by(
        holds(p.operator, env.ctx.iob_units, env.limit(p)),
        ContextFact::Insulin,
        UnknownReason::Absent,
    )
}

pub(super) fn cob(p: &ComparePayload, env: &Env) -> Held {
    unknown_by(
        holds(p.operator, env.ctx.cob_grams, env.limit(p)),
        ContextFact::Carbs,
        UnknownReason::Absent,
    )
}

/// A lower-bound reading ("50+") only proves the reservoir holds at least that
/// much, so a comparison is decided only where every amount from the bound up
/// gives the same answer: `>` / `>=` are true once the bound satisfies them,
/// `<` / `<=` / `==` false once the bound is past them, and anything else is
/// unknown.
pub(super) fn reservoir(p: &ComparePayload, env: &Env) -> Held {
    let Some(bound) = env.ctx.reservoir_units else {
        return Held::unknown(ContextFact::Pump, UnknownReason::Absent);
    };
    let limit = env.limit(p);
    if !env.ctx.reservoir_is_lower_bound {
        return Truth::from(p.operator.apply(bound, limit)).into();
    }
    let decided = match p.operator {
        CmpOp::Gt | CmpOp::Ge => p.operator.apply(bound, limit).then_some(true),
        CmpOp::Lt => (bound >= limit).then_some(false),
        CmpOp::Le | CmpOp::Eq => (bound > limit).then_some(false),
    };
    unknown_by(
        Truth::of(decided),
        ContextFact::Pump,
        UnknownReason::Inexact,
    )
}

/// No `has_ever_*` guard: no active temp is false, since the condition only
/// concerns active temps. `rate` compares U/hr; `percent_of_scheduled`
/// compares `rate / scheduled_rate * 100` in decimal, unknown when the
/// scheduled rate is null or zero or the percentage overflows. A temp's rate
/// stands for the whole temp, so `rate` has held since it started; the
/// scheduled rate follows the schedule within it, so `percent_of_scheduled`
/// knows no start.
pub(super) fn temp_basal(p: &TempBasalPayload, env: &Env) -> Held {
    let Some(temp) = &env.ctx.active_temp_basal else {
        return Truth::False.into();
    };
    let actual = match p.metric {
        TempBasalMetric::Rate => Some(temp.rate),
        TempBasalMetric::PercentOfScheduled => temp
            .scheduled_rate
            .and_then(|scheduled| temp.rate.checked_div(scheduled))
            .and_then(|ratio| ratio.checked_mul(Decimal::ONE_HUNDRED)),
    };
    let since = (p.metric == TempBasalMetric::Rate).then_some(temp.started_at);
    Held::since(holds(p.operator, actual, env.limit(p)), since).because(UnknownFacts::of(
        ContextFact::Insulin,
        UnknownReason::Absent,
    ))
}
