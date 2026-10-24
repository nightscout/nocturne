//! Insulin/treatment leaves: iob, cob, reservoir, temp_basal.

use rust_decimal::Decimal;

use super::Env;
use crate::enums::{CmpOp, TempBasalMetric, holds};
use crate::model::{ComparePayload, TempBasalPayload};

pub(super) fn iob(p: &ComparePayload, env: &Env) -> bool {
    holds(p.operator, env.ctx.iob_units, p.value)
}

pub(super) fn cob(p: &ComparePayload, env: &Env) -> bool {
    holds(p.operator, env.ctx.cob_grams, p.value)
}

/// A lower-bound reading ("50+") only proves the reservoir holds at least that
/// much: `>` / `>=` can be confirmed against the bound; `<` / `<=` / `==` cannot.
pub(super) fn reservoir(p: &ComparePayload, env: &Env) -> bool {
    if env.ctx.reservoir_is_lower_bound && !matches!(p.operator, CmpOp::Gt | CmpOp::Ge) {
        return false;
    }
    holds(p.operator, env.ctx.reservoir_units, p.value)
}

/// No `has_ever_*` guard: the condition only concerns active temps. `rate`
/// compares U/hr; `percent_of_scheduled` compares `rate / scheduled_rate * 100`
/// in decimal, false when the scheduled rate is null or zero or the percentage
/// overflows.
pub(super) fn temp_basal(p: &TempBasalPayload, env: &Env) -> bool {
    let Some(temp) = &env.ctx.active_temp_basal else {
        return false;
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
