//! Insulin/treatment leaves: iob, cob, reservoir, temp_basal.

use rust_decimal::Decimal;

use super::Env;
use super::glucose::compare_optional;
use crate::compare::compare;
use crate::model::{ComparePayload, TempBasalPayload};

pub(super) fn iob(p: &ComparePayload, env: &Env) -> bool {
    compare_optional(p, env.ctx.iob_units)
}

pub(super) fn cob(p: &ComparePayload, env: &Env) -> bool {
    compare_optional(p, env.ctx.cob_grams)
}

/// A lower-bound reading ("50+") only proves the reservoir holds at least that
/// much: `>` / `>=` can be confirmed against the bound; `<` / `<=` / `==` cannot.
pub(super) fn reservoir(p: &ComparePayload, env: &Env) -> bool {
    if env.ctx.reservoir_is_lower_bound && !matches!(p.operator.as_deref(), Some(">") | Some(">="))
    {
        return false;
    }
    compare_optional(p, env.ctx.reservoir_units)
}

/// No `HasEver*` guard by design — the condition only concerns active temps.
/// `rate` compares U/hr; `percent_of_scheduled` compares
/// `Rate / ScheduledRate * 100` (decimal division), false when the scheduled
/// rate is null or zero. An undefined metric ordinal → false.
pub(super) fn temp_basal(p: &TempBasalPayload, env: &Env) -> bool {
    let Some(temp) = &env.ctx.active_temp_basal else {
        return false;
    };
    let actual = match p.metric {
        0 => Some(temp.rate),
        1 => match temp.scheduled_rate {
            Some(scheduled) if scheduled != Decimal::ZERO => {
                Some(temp.rate / scheduled * Decimal::from(100))
            }
            _ => None,
        },
        _ => None,
    };
    match actual {
        Some(actual) => compare(actual, p.operator.as_deref(), p.value),
        None => false,
    }
}
