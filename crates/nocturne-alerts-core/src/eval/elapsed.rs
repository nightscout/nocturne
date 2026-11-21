//! The wall-clock leaves (engine-semantics.md §5.1) as what each compares
//! against `now`, read once by evaluation and once by the next-change scan
//! (§5.3), so the two cannot disagree about an anchor, a guard or a numeric.

use chrono::{DateTime, TimeDelta, Utc};
use rust_decimal::Decimal;

use crate::compare::{Unit, decimal_from_f64_cs, elapsed};
use crate::context::SensorContext;
use crate::enums::CmpOp;
use crate::model::Payload;
use crate::truth::Truth;

/// A wall-clock leaf's truth for one context: fixed whatever `now` is, or an
/// elapsed time compared with a bound. A leaf without its anchor is fixed, so
/// an unknown one contributes no next change (engine-semantics.md §5.3).
#[derive(Debug, Clone, Copy)]
pub(crate) enum ClockLeaf {
    Fixed(Truth),
    Measured(Elapsed),
    /// False until the bound holds, unknown from then on.
    UnknownOnceHeld(Elapsed),
}

impl ClockLeaf {
    pub(crate) fn holds(self, now: DateTime<Utc>) -> Truth {
        match self {
            ClockLeaf::Fixed(value) => value,
            ClockLeaf::Measured(e) => e.holds(now).into(),
            ClockLeaf::UnknownOnceHeld(e) => {
                if e.holds(now) {
                    Truth::Unknown
                } else {
                    Truth::False
                }
            }
        }
    }

    /// The comparison whose flips are this leaf's changes.
    pub(crate) fn elapsed(self) -> Option<Elapsed> {
        match self {
            ClockLeaf::Fixed(_) => None,
            ClockLeaf::Measured(e) | ClockLeaf::UnknownOnceHeld(e) => Some(e),
        }
    }
}

/// The time since `anchor` compared with a bound.
#[derive(Debug, Clone, Copy)]
pub(crate) struct Elapsed {
    pub(crate) anchor: DateTime<Utc>,
    pub(crate) measure: Measure,
}

/// How an elapsed time is measured and compared (engine-semantics.md §1.3).
#[derive(Debug, Clone, Copy)]
pub(crate) enum Measure {
    /// `(decimal)` of the double elapsed time in `unit`, compared exactly.
    Decimal {
        unit: Unit,
        op: CmpOp,
        threshold: Decimal,
    },
    /// Fractional minutes as a double.
    Minutes { op: CmpOp, minutes: f64 },
    /// `now - anchor >= bound` as exact durations.
    AtLeast(TimeDelta),
    /// `now - anchor > bound` as exact durations.
    Over(TimeDelta),
}

impl Elapsed {
    /// Held for at least `minutes` fractional minutes: `for_minutes` and
    /// `sustained`.
    pub(crate) fn held_for(anchor: DateTime<Utc>, minutes: i32) -> Self {
        Elapsed {
            anchor,
            measure: Measure::Minutes {
                op: CmpOp::Ge,
                minutes: f64::from(minutes),
            },
        }
    }

    pub(crate) fn holds(self, now: DateTime<Utc>) -> bool {
        match self.measure {
            Measure::Decimal {
                unit,
                op,
                threshold,
            } => elapsed(now, self.anchor, unit)
                .and_then(decimal_from_f64_cs)
                .is_some_and(|actual| op.apply(actual, threshold)),
            Measure::Minutes { op, minutes } => {
                elapsed(now, self.anchor, Unit::Minutes).is_some_and(|m| op.apply(m, minutes))
            }
            Measure::AtLeast(bound) => now.signed_duration_since(self.anchor) >= bound,
            Measure::Over(bound) => now.signed_duration_since(self.anchor) > bound,
        }
    }

    /// The comparisons, each false then true as `now` advances, whose flips
    /// are every instant this one can change at. `<` and `>=` change where
    /// the elapsed time reaches the bound, `<=` and `>` where it passes it,
    /// `==` at both.
    pub(crate) fn monotone_parts(self) -> impl Iterator<Item = Elapsed> {
        let ops: &[CmpOp] = match self.measure {
            Measure::AtLeast(_) | Measure::Over(_) => &[CmpOp::Ge],
            Measure::Decimal { op, .. } | Measure::Minutes { op, .. } => match op {
                CmpOp::Lt | CmpOp::Ge => &[CmpOp::Ge],
                CmpOp::Le | CmpOp::Gt => &[CmpOp::Gt],
                CmpOp::Eq => &[CmpOp::Ge, CmpOp::Gt],
            },
        };
        ops.iter().map(move |&op| Elapsed {
            anchor: self.anchor,
            measure: match self.measure {
                Measure::Decimal {
                    unit, threshold, ..
                } => Measure::Decimal {
                    unit,
                    op,
                    threshold,
                },
                Measure::Minutes { minutes, .. } => Measure::Minutes { op, minutes },
                measure @ (Measure::AtLeast(_) | Measure::Over(_)) => measure,
            },
        })
    }
}

/// A wall-clock leaf (`wall_clock::is_wall_clock`) as it stands in `ctx`;
/// `None` for any other kind.
pub(crate) fn clock_leaf(payload: &Payload, ctx: &SensorContext) -> Option<ClockLeaf> {
    let leaf = match payload {
        Payload::SignalLoss(p) => super::signal::signal_loss(p, ctx),
        Payload::Staleness(p) => super::glucose::staleness(p, ctx),
        Payload::SiteAge(p) => super::device::site_age(p, ctx),
        Payload::SensorAge(p) => super::device::sensor_age(p, ctx),
        Payload::TrackerAge(p) => super::device::tracker_age(p, ctx),
        Payload::LoopStale(p) => super::device::loop_stale(p, ctx.last_aps_cycle_at, ctx),
        Payload::LoopEnactionStale(p) => super::device::loop_stale(p, ctx.last_aps_enacted_at, ctx),
        Payload::PumpSuspended(p) => super::device::pump_suspended(p, ctx),
        Payload::AlertState(p) => super::spans::alert_state(p, ctx),
        Payload::OverrideActive(p) => super::spans::override_active(p, ctx),
        Payload::DoNotDisturb(p) => super::spans::do_not_disturb(p, ctx),
        Payload::PumpState(p) => super::spans::pump_state(p, ctx),
        Payload::StateSpanActive(p) => super::spans::state_span_active(p, ctx),
        Payload::TimeSinceLastCarb(p) => super::clock::time_since(p, ctx.last_carb_at),
        Payload::TimeSinceLastBolus(p) => super::clock::time_since(p, ctx.last_bolus_at),
        Payload::Threshold(_)
        | Payload::RateOfChange(_)
        | Payload::Composite(_)
        | Payload::Not(_)
        | Payload::Sustained(_)
        | Payload::Predicted(_)
        | Payload::Trend(_)
        | Payload::TimeOfDay(_)
        | Payload::Iob(_)
        | Payload::Cob(_)
        | Payload::Reservoir(_)
        | Payload::PumpBattery(_)
        | Payload::TempBasal(_)
        | Payload::UploaderBattery(_)
        | Payload::TransmitterBattery(_)
        | Payload::SensitivityRatio(_)
        | Payload::GlucoseBucket(_)
        | Payload::DayOfWeek(_)
        | Payload::SleepSessionActive(_) => return None,
    };
    Some(leaf)
}

/// A span leaf with `for_minutes` (engine-semantics.md §3): its presence
/// matches `is_active` and, on the active side, it has held for `for_minutes`
/// when that is set.
pub(super) fn active_for(
    is_active: bool,
    for_minutes: Option<i32>,
    started_at: Option<DateTime<Utc>>,
) -> ClockLeaf {
    match started_at {
        Some(at) if is_active => for_minutes.map_or(ClockLeaf::Fixed(Truth::True), |m| {
            ClockLeaf::Measured(Elapsed::held_for(at, m))
        }),
        Some(_) => ClockLeaf::Fixed(Truth::False),
        None => ClockLeaf::Fixed((!is_active).into()),
    }
}

/// An elapsed time in `unit` converted to decimal and compared.
pub(super) fn decimal(
    anchor: DateTime<Utc>,
    unit: Unit,
    op: CmpOp,
    threshold: impl Into<Decimal>,
) -> ClockLeaf {
    ClockLeaf::Measured(Elapsed {
        anchor,
        measure: Measure::Decimal {
            unit,
            op,
            threshold: threshold.into(),
        },
    })
}
