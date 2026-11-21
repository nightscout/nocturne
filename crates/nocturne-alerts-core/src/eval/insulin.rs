//! Insulin/treatment leaves: iob, cob, reservoir, temp_basal.

use rust_decimal::Decimal;

use super::Env;
use crate::enums::{CmpOp, TempBasalMetric, holds};
use crate::model::{ComparePayload, TempBasalPayload};
use crate::truth::Truth;

pub(super) fn iob(p: &ComparePayload, env: &Env) -> Truth {
    holds(p.operator, env.ctx.iob_units, p.value)
}

pub(super) fn cob(p: &ComparePayload, env: &Env) -> Truth {
    holds(p.operator, env.ctx.cob_grams, p.value)
}

/// A lower-bound reading ("50+") only proves the reservoir holds at least that
/// much, so a comparison is decided only where every amount from the bound up
/// gives the same answer: `>` / `>=` are true once the bound satisfies them,
/// `<` / `<=` / `==` false once the bound is past them, and anything else is
/// unknown.
pub(super) fn reservoir(p: &ComparePayload, env: &Env) -> Truth {
    let Some(bound) = env.ctx.reservoir_units else {
        return Truth::Unknown;
    };
    if !env.ctx.reservoir_is_lower_bound {
        return p.operator.apply(bound, p.value).into();
    }
    let decided = match p.operator {
        CmpOp::Gt | CmpOp::Ge => p.operator.apply(bound, p.value).then_some(true),
        CmpOp::Lt => (bound >= p.value).then_some(false),
        CmpOp::Le | CmpOp::Eq => (bound > p.value).then_some(false),
    };
    Truth::of(decided)
}

/// No `has_ever_*` guard: no active temp is false, since the condition only
/// concerns active temps. `rate` compares U/hr; `percent_of_scheduled`
/// compares `rate / scheduled_rate * 100` in decimal, unknown when the
/// scheduled rate is null or zero or the percentage overflows.
pub(super) fn temp_basal(p: &TempBasalPayload, env: &Env) -> Truth {
    let Some(temp) = &env.ctx.active_temp_basal else {
        return Truth::False;
    };
    let actual = match p.metric {
        TempBasalMetric::Rate => Some(temp.rate),
        TempBasalMetric::PercentOfScheduled => temp
            .scheduled_rate
            .and_then(|scheduled| temp.rate.checked_div(scheduled))
            .and_then(|ratio| ratio.checked_mul(Decimal::ONE_HUNDRED)),
    };
    holds(p.operator, actual, p.value)
}
