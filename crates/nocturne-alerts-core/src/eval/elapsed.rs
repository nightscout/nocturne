//! The wall-clock leaves (engine-semantics.md §5.1) as what each compares
//! against `now`, read once by evaluation and once by the next-change scan
//! (§5.3), so the two cannot disagree about an anchor, a guard or a numeric.

use chrono::{DateTime, TimeDelta, Utc};
use rust_decimal::Decimal;

use crate::clock_anomaly::{CLOCK_TOLERANCE, ClockAnchor, since_anchor};
use crate::compare::{Unit, decimal_from_f64_cs, in_unit};
use crate::context::SensorContext;
use crate::enums::CmpOp;
use crate::facts::ContextFact;
use crate::model::Payload;
use crate::truth::Truth;
use crate::unknown::{UnknownFacts, UnknownReason};

use super::Held;

/// A wall-clock leaf's truth for one context: fixed whatever `now` is, or an
/// elapsed time compared with a bound. A leaf without its anchor is fixed or
/// unknown, so it contributes no next change (engine-semantics.md §5.3).
#[derive(Debug, Clone, Copy)]
pub(crate) enum ClockLeaf {
    Fixed(Truth),
    /// Unknown whatever `now` is, by its fact.
    Unknown(ContextFact, UnknownReason),
    /// True, and has been since the span it reads began.
    Began(DateTime<Utc>),
    Measured(Elapsed),
    /// False until the bound holds, unknown from then on: a pump mode the
    /// crate cannot name (engine-semantics.md §3).
    UnknownOnceHeld(Elapsed),
}

impl ClockLeaf {
    /// The truth at `now` with the instant it has held since, where the leaf
    /// knows one (engine-semantics.md §3), and the facts that leave it
    /// unknown.
    pub(crate) fn held(self, now: DateTime<Utc>) -> Held {
        match self {
            ClockLeaf::Fixed(value) => value.into(),
            ClockLeaf::Unknown(fact, reason) => Held::unknown(fact, reason),
            ClockLeaf::Began(at) => Held::since(Truth::True, Some(at)),
            ClockLeaf::Measured(e) => {
                Held::since(e.truth(now), e.began(now)).because(e.unknown_by())
            }
            ClockLeaf::UnknownOnceHeld(e) => match e.truth(now) {
                Truth::False => Truth::False.into(),
                Truth::True => Held::unknown(ContextFact::StateSpans, UnknownReason::Unrecognised),
                Truth::Unknown => Held::from(Truth::Unknown).because(e.unknown_by()),
            },
        }
    }

    /// The comparison whose flips are this leaf's changes.
    pub(crate) fn elapsed(self) -> Option<Elapsed> {
        match self {
            ClockLeaf::Fixed(_) | ClockLeaf::Unknown(..) | ClockLeaf::Began(_) => None,
            ClockLeaf::Measured(e) | ClockLeaf::UnknownOnceHeld(e) => Some(e),
        }
    }
}

/// The time since `anchor` compared with a bound.
#[derive(Debug, Clone, Copy)]
pub(crate) struct Elapsed {
    pub(crate) anchor: DateTime<Utc>,
    /// The name an anchor ahead of `now` is reported under (§5.4); `None` for
    /// one meant to be ahead, a scheduled event or an arrival, whose elapsed
    /// time is negative until it.
    pub(crate) source: Option<ClockAnchor>,
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
    pub(crate) fn held_for(anchor: DateTime<Utc>, minutes: i32, source: ClockAnchor) -> Self {
        Elapsed {
            anchor,
            source: Some(source),
            measure: Measure::Minutes {
                op: CmpOp::Ge,
                minutes: f64::from(minutes),
            },
        }
    }

    /// The time since the anchor, or how far beyond [`CLOCK_TOLERANCE`]
    /// ahead of `now` it is.
    fn since(self, now: DateTime<Utc>) -> Result<TimeDelta, TimeDelta> {
        let d = now.signed_duration_since(self.anchor);
        match self.source {
            Some(_) => since_anchor(d),
            None => Ok(d),
        }
    }

    /// Unknown while the anchor is beyond the tolerance ahead.
    pub(crate) fn truth(self, now: DateTime<Utc>) -> Truth {
        self.since(now)
            .map_or(Truth::Unknown, |d| self.compares(d).into())
    }

    /// The comparison with an anchor beyond the tolerance read as one within
    /// it: false then true as `now` advances wherever the measure is, so the
    /// next-change search can bisect it.
    pub(crate) fn reached(self, now: DateTime<Utc>) -> bool {
        self.compares(self.since(now).unwrap_or(TimeDelta::zero()))
    }

    /// What an unknown comparison is unknown by: its anchor beyond the
    /// tolerance ahead.
    pub(crate) fn unknown_by(self) -> UnknownFacts {
        self.source
            .map_or_else(UnknownFacts::default, UnknownFacts::ahead_of_clock)
    }

    /// The anchor and how far ahead of `now` it is, when beyond the tolerance.
    pub(crate) fn ahead(self, now: DateTime<Utc>) -> Option<(ClockAnchor, TimeDelta)> {
        self.source.zip(self.since(now).err())
    }

    /// When an anchor beyond the tolerance ahead comes within it, so the
    /// comparison is known; `None` when it is known at `now`.
    pub(crate) fn known_from(self, now: DateTime<Utc>) -> Option<DateTime<Utc>> {
        self.ahead(now)
            .and_then(|_| self.anchor.checked_sub_signed(CLOCK_TOLERANCE))
    }

    /// The first instant at or before `now` from which the comparison has
    /// held, when it holds at `now` and grows true with the elapsed time
    /// (`>=`, `>`): the anchor when it held there, else where the elapsed time
    /// crossed the bound, rounded up to the tick so a host that keeps ticks
    /// reads no earlier instant. `None` for `<`, `<=` and `==`, which hold
    /// before a bound rather than from one.
    pub(crate) fn began(self, now: DateTime<Utc>) -> Option<DateTime<Utc>> {
        let rising = match self.measure {
            Measure::AtLeast(_) | Measure::Over(_) => true,
            Measure::Decimal { op, .. } | Measure::Minutes { op, .. } => {
                matches!(op, CmpOp::Ge | CmpOp::Gt)
            }
        };
        if !rising || !self.truth(now).is_true() {
            return None;
        }
        if self.reached(self.anchor) {
            return Some(self.anchor);
        }
        first_true_between(self.anchor, now, |t| self.reached(t)).and_then(ceil_to_tick)
    }

    fn compares(self, d: TimeDelta) -> bool {
        match self.measure {
            Measure::Decimal {
                unit,
                op,
                threshold,
            } => in_unit(d, unit)
                .and_then(decimal_from_f64_cs)
                .is_some_and(|actual| op.apply(actual, threshold)),
            Measure::Minutes { op, minutes } => {
                in_unit(d, Unit::Minutes).is_some_and(|m| op.apply(m, minutes))
            }
            Measure::AtLeast(bound) => d >= bound,
            Measure::Over(bound) => d > bound,
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
            ..self
        })
    }
}

/// Bisects `(before, after]`, `holds` false at `before` and true at `after`,
/// to the nanosecond.
pub(crate) fn first_true_between(
    mut before: DateTime<Utc>,
    mut after: DateTime<Utc>,
    holds: impl Fn(DateTime<Utc>) -> bool,
) -> Option<DateTime<Utc>> {
    while let Some(half) = after.signed_duration_since(before).checked_div(2)
        && half > TimeDelta::zero()
    {
        let mid = before.checked_add_signed(half)?;
        if holds(mid) {
            after = mid;
        } else {
            before = mid;
        }
    }
    Some(after)
}

/// `at` rounded up to the 100 ns tick grid ([`crate::next_change::RESOLUTION`]).
fn ceil_to_tick(at: DateTime<Utc>) -> Option<DateTime<Utc>> {
    let past_tick = at.timestamp_subsec_nanos() % 100;
    if past_tick == 0 {
        return Some(at);
    }
    at.checked_add_signed(TimeDelta::nanoseconds(i64::from(
        100u32.saturating_sub(past_tick),
    )))
}

/// A wall-clock leaf (`wall_clock::is_wall_clock`) as it stands in `ctx` at
/// `now`, for a rule enabled at `enabled_at`; `None` for any other kind.
/// `now` decides only whether the sensor's state has lapsed: every elapsed
/// time is measured from the returned leaf.
pub(crate) fn clock_leaf(
    payload: &Payload,
    ctx: &SensorContext,
    enabled_at: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
) -> Option<ClockLeaf> {
    let leaf = match payload {
        Payload::SignalLoss(p) => super::signal::signal_loss(p, ctx, enabled_at),
        Payload::Staleness(p) => super::glucose::staleness(p, ctx, enabled_at),
        Payload::SiteAge(p) => super::device::site_age(p, ctx),
        Payload::SensorAge(p) => super::device::sensor_age(p, ctx),
        Payload::TrackerAge(p) => super::device::tracker_age(p, ctx),
        Payload::LoopStale(p) => {
            super::device::loop_stale(p, ctx.last_aps_cycle_at, ClockAnchor::LastApsCycleAt, ctx)
        }
        Payload::LoopEnactionStale(p) => super::device::loop_stale(
            p,
            ctx.last_aps_enacted_at,
            ClockAnchor::LastApsEnactedAt,
            ctx,
        ),
        Payload::PumpSuspended(p) => super::device::pump_suspended(p, ctx),
        Payload::AlertState(p) => super::spans::alert_state(p, ctx),
        Payload::OverrideActive(p) => super::spans::override_active(p, ctx),
        Payload::DoNotDisturb(p) => super::spans::do_not_disturb(p, ctx),
        Payload::PumpState(p) => super::spans::pump_state(p, ctx),
        Payload::StateSpanActive(p) => super::spans::state_span_active(p, ctx),
        Payload::SensorState(p) => super::spans::sensor_state(p, ctx, now),
        Payload::TimeSinceLastCarb(p) => {
            super::clock::time_since(p, ctx.last_carb_at, ClockAnchor::LastCarbAt)
        }
        Payload::TimeSinceLastBolus(p) => {
            super::clock::time_since(p, ctx.last_bolus_at, ClockAnchor::LastBolusAt)
        }
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
        | Payload::EventualGlucose(_)
        | Payload::CarbsRequired(_)
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
    source: ClockAnchor,
) -> ClockLeaf {
    match started_at {
        Some(at) if is_active => for_minutes.map_or(ClockLeaf::Began(at), |m| {
            ClockLeaf::Measured(Elapsed::held_for(at, m, source))
        }),
        Some(_) => ClockLeaf::Fixed(Truth::False),
        None => ClockLeaf::Fixed((!is_active).into()),
    }
}

/// An elapsed time in `unit` converted to decimal and compared.
pub(super) fn decimal(
    anchor: DateTime<Utc>,
    source: Option<ClockAnchor>,
    unit: Unit,
    op: CmpOp,
    threshold: impl Into<Decimal>,
) -> ClockLeaf {
    ClockLeaf::Measured(Elapsed {
        anchor,
        source,
        measure: Measure::Decimal {
            unit,
            op,
            threshold: threshold.into(),
        },
    })
}
