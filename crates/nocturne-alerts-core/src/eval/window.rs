//! Windowed glucose leaves: glucose_change, glucose_count, glucose_extreme
//! (engine-semantics.md §3, windows).

use std::collections::BTreeMap;

use chrono::{DateTime, TimeDelta, Utc};
use rust_decimal::Decimal;

use super::glucose::judged_value;
use super::{Env, Held};
use crate::enums::{CmpOp, WindowExtreme};
use crate::facts::ContextFact;
use crate::model::{
    Comparison, GlucoseChangePayload, GlucoseCountPayload, GlucoseExtremePayload, Tally,
    WindowMinutes,
};
use crate::truth::Truth;
use crate::unknown::UnknownReason;

/// The readings in a window that ends at the newest reading, oldest first,
/// and whether a stretch longer than the freshness bound has none.
struct Window {
    values: Vec<Decimal>,
    /// The oldest reading is more than the bound after the window opens.
    starts_late: bool,
    /// Two readings in a row are more than the bound apart.
    gapped: bool,
}

impl Window {
    /// Whether readings that a stretch with none could hold might change
    /// the answer.
    fn incomplete(&self) -> bool {
        self.starts_late || self.gapped
    }
}

/// The window of `minutes` ending at the newest reading, or the unknown that
/// leaves it unread: the reading as `threshold` judges it, then the series.
/// The series must hold the newest reading itself, so a host that sent
/// another stream's readings, or none, is not read as a window.
fn window(minutes: WindowMinutes, env: &mut Env) -> Result<Window, Held> {
    judged_value(env.ctx.latest_value, env)?;
    let newest = env
        .ctx
        .latest_timestamp
        .ok_or_else(|| Held::unknown(ContextFact::Glucose, UnknownReason::Absent))?;
    let opens = newest
        .checked_sub_signed(TimeDelta::minutes(i64::from(minutes.get())))
        .ok_or_else(absent)?;
    // One value per instant, the last one listed (engine-semantics.md §4).
    let points: BTreeMap<DateTime<Utc>, Decimal> = env
        .ctx
        .glucose_series
        .iter()
        .filter(|p| (opens..=newest).contains(&p.at))
        .map(|p| (p.at, p.mgdl))
        .collect();
    if !points.contains_key(&newest) {
        return Err(absent());
    }
    let bound = env.glucose_freshness;
    let times: Vec<DateTime<Utc>> = points.keys().copied().collect();
    let starts_late = times
        .first()
        .is_some_and(|&first| first.signed_duration_since(opens) > bound);
    let gapped = times
        .windows(2)
        .any(|pair| matches!(pair, [a, b] if b.signed_duration_since(*a) > bound));
    Ok(Window {
        values: points.into_values().collect(),
        starts_late,
        gapped,
    })
}

fn absent() -> Held {
    Held::unknown(ContextFact::GlucoseSeries, UnknownReason::Absent)
}

/// A windowed leaf's truth: held since the newest reading, as a glucose leaf
/// is, and unknown by the series when readings it lacks could decide it.
fn decided(truth: Truth, env: &Env) -> Held {
    match truth {
        Truth::Unknown => Held::unknown(ContextFact::GlucoseSeries, UnknownReason::Inexact),
        known => Held::since(known, env.ctx.latest_timestamp),
    }
}

/// Whether `op` against `limit` holds for every value from `low` to `high`
/// (true), for none (false), or for some (unknown). An open end is `None`.
fn over_range(op: CmpOp, low: Option<Decimal>, high: Option<Decimal>, limit: Decimal) -> Truth {
    let every = |end: Option<Decimal>, test: fn(Decimal, Decimal) -> bool| {
        end.is_some_and(|end| test(end, limit))
    };
    let (all, none) = match op {
        CmpOp::Gt => (every(low, |v, l| v > l), every(high, |v, l| v <= l)),
        CmpOp::Ge => (every(low, |v, l| v >= l), every(high, |v, l| v < l)),
        CmpOp::Lt => (every(high, |v, l| v < l), every(low, |v, l| v >= l)),
        CmpOp::Le => (every(high, |v, l| v <= l), every(low, |v, l| v > l)),
        CmpOp::Eq => (
            low == Some(limit) && high == Some(limit),
            every(low, |v, l| v > l) || every(high, |v, l| v < l),
        ),
    };
    if all {
        Truth::True
    } else if none {
        Truth::False
    } else {
        Truth::Unknown
    }
}

/// The newest reading less the oldest one in the window. Fewer than two
/// readings is no change; an oldest reading that comes late measures a
/// shorter time than the rule names, so it is unknown.
pub(super) fn glucose_change(p: &GlucoseChangePayload, env: &mut Env) -> Held {
    let window = match window(p.window_minutes, env) {
        Ok(window) => window,
        Err(unknown) => return unknown,
    };
    let (Some(&oldest), Some(&newest), true) = (
        window.values.first(),
        window.values.last(),
        window.values.len() >= 2,
    ) else {
        return absent();
    };
    if window.starts_late {
        return decided(Truth::Unknown, env);
    }
    let truth = newest.checked_sub(oldest).map_or(Truth::Unknown, |change| {
        p.operator.apply(change, env.limit(p)).into()
    });
    decided(truth, env)
}

/// The lowest or highest reading in the window. A stretch with no readings
/// could hold one lower or higher, so the extreme read is a bound: it
/// decides the comparison only where every value past it gives the same
/// answer.
pub(super) fn glucose_extreme(p: &GlucoseExtremePayload, env: &mut Env) -> Held {
    let window = match window(p.window_minutes, env) {
        Ok(window) => window,
        Err(unknown) => return unknown,
    };
    let values = window.values.iter().copied();
    let extreme = match p.of {
        WindowExtreme::Min => values.min(),
        WindowExtreme::Max => values.max(),
    };
    let Some(extreme) = extreme else {
        return absent();
    };
    let open = window.incomplete().then_some(());
    let (low, high) = match p.of {
        WindowExtreme::Min => (open.map_or(Some(extreme), |()| None), Some(extreme)),
        WindowExtreme::Max => (Some(extreme), open.map_or(Some(extreme), |()| None)),
    };
    decided(over_range(p.operator, low, high, env.limit(p)), env)
}

/// How many of the window's readings meet `reading`, or what percent of
/// them do. A stretch with no readings could hold any number more, of either
/// kind, so a count read is a lower bound and a percent could be anything.
pub(super) fn glucose_count(p: &GlucoseCountPayload, env: &mut Env) -> Held {
    let window = match window(p.window_minutes, env) {
        Ok(window) => window,
        Err(unknown) => return unknown,
    };
    let Comparison { operator, value } = p.reading;
    let met = window
        .values
        .iter()
        .filter(|&&v| operator.apply(v, value))
        .count();
    let truth = match p.count {
        Tally::Count(c) => {
            let met = Decimal::from(met);
            let high = (!window.incomplete()).then_some(met);
            over_range(c.operator, Some(met), high, c.value)
        }
        Tally::Percent(c) if window.incomplete() => over_range(
            c.operator,
            Some(Decimal::ZERO),
            Some(Decimal::ONE_HUNDRED),
            c.value,
        ),
        Tally::Percent(c) => {
            let percent = Decimal::from(met)
                .checked_mul(Decimal::ONE_HUNDRED)
                .and_then(|m| m.checked_div(Decimal::from(window.values.len())));
            percent.map_or(Truth::Unknown, |percent| {
                c.operator.apply(percent, c.value).into()
            })
        }
    };
    decided(truth, env)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn d(v: i64) -> Decimal {
        Decimal::from(v)
    }

    #[test]
    fn a_point_range_is_the_plain_comparison() {
        for op in [CmpOp::Gt, CmpOp::Ge, CmpOp::Lt, CmpOp::Le, CmpOp::Eq] {
            for (v, l) in [(1, 2), (2, 2), (3, 2)] {
                assert_eq!(
                    over_range(op, Some(d(v)), Some(d(v)), d(l)),
                    Truth::from(op.apply(d(v), d(l))),
                    "{op:?} {v} {l}"
                );
            }
        }
    }

    #[test]
    fn an_open_end_decides_only_what_every_value_past_it_decides() {
        // A highest reading of 260 with a stretch missing: at least 260.
        assert_eq!(
            over_range(CmpOp::Gt, Some(d(260)), None, d(250)),
            Truth::True
        );
        assert_eq!(
            over_range(CmpOp::Gt, Some(d(240)), None, d(250)),
            Truth::Unknown
        );
        assert_eq!(
            over_range(CmpOp::Lt, Some(d(260)), None, d(250)),
            Truth::False
        );
        assert_eq!(
            over_range(CmpOp::Lt, Some(d(240)), None, d(250)),
            Truth::Unknown
        );
        assert_eq!(
            over_range(CmpOp::Eq, Some(d(260)), None, d(250)),
            Truth::False
        );
        assert_eq!(
            over_range(CmpOp::Eq, Some(d(250)), None, d(250)),
            Truth::Unknown
        );
        // A lowest reading of 60 with a stretch missing: at most 60.
        assert_eq!(over_range(CmpOp::Lt, None, Some(d(60)), d(70)), Truth::True);
        assert_eq!(
            over_range(CmpOp::Ge, None, Some(d(60)), d(70)),
            Truth::False
        );
        assert_eq!(
            over_range(CmpOp::Le, None, Some(d(80)), d(70)),
            Truth::Unknown
        );
    }
}
