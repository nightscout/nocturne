//! When a rule's evaluation can next differ if every context fact and all
//! state stand still and only `now` advances (engine-semantics.md §5.3).
//!
//! Each flip is found by searching the very comparison evaluation makes, so
//! the double and decimal rounding of §1.3 moves the instant with it rather
//! than being estimated.

use chrono::{
    DateTime, Days, FixedOffset, LocalResult, NaiveDateTime, NaiveTime, Offset, TimeDelta,
    TimeZone, Utc,
};
use chrono_tz::Tz;
use uuid::Uuid;

use crate::clock_anomaly::ClockAnchor;
use crate::context::SensorContext;
use crate::engine::{EngineState, Rule};
use crate::enums::WireEnum;
use crate::eval::clock::{local, tenant_zone, time_of_day_zone};
use crate::eval::determination::determination_expiry;
use crate::eval::elapsed::{ClockLeaf, Elapsed, clock_leaf, first_true_between};
use crate::eval::glucose::freshness_expiry;
use crate::excursion::{TrackerStateKind, hysteresis_window};
use crate::facts::{ContextFact, leaf_reads};
use crate::leaf_identity::{collect_leaves, nodes_with_paths};
use crate::model::{Node, Payload};
use crate::paths::AUTO_RESOLVE_ROOT;
use crate::sustained::TimerStore;

/// The engine's time resolution, one .NET tick: the next change reported
/// when the only change due is at `now` itself.
pub const RESOLUTION: TimeDelta = TimeDelta::nanoseconds(100);

/// The earliest instant strictly after `now` at which an evaluation of
/// `rule` from `state`, the state this one left, can do anything other than
/// repeat this one's root and leaves with no transition, or `None` when none
/// can. A lower bound on the 100 ns tick grid: nothing observable changes at
/// a tick before it.
///
/// `evaluated_open` is whether this evaluation found the excursion open. A
/// body with a clear margin whose excursion this evaluation closed reads its
/// margined leaves against the plain limit on the next one, which can answer
/// false where this one read true, so that one is due at once. After an
/// opening the next reads at least as true (§3), so nothing is due for it.
pub(crate) fn next_change_at(
    rule: &Rule,
    body: &Node,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &EngineState,
    evaluated_open: bool,
) -> Option<DateTime<Utc>> {
    let mut scan = Scan {
        now,
        rule_id: rule.id,
        glucose_freshness: rule.glucose_freshness,
        enabled_at: rule.enabled_at,
        ctx,
        timers: &state.timers,
        earliest: None,
    };
    scan.tree(body, rule.condition_type.name());
    if rule.auto_resolve_enabled
        && let Some(tree) = rule.parse_auto_resolve()
    {
        scan.tree(&tree, AUTO_RESOLVE_ROOT);
    }
    let due_now = now.checked_add_signed(TimeDelta::nanoseconds(1));
    if evaluated_open && !state.tracker.excursion_open(rule.id) && takes_margin(body) {
        scan.candidate(due_now);
    }
    if let Some(tracker) = state.tracker.state(rule.id) {
        match tracker.state {
            TrackerStateKind::Hysteresis => {
                if let Some(window) = hysteresis_window(tracker, rule.hysteresis_minutes) {
                    if window.truth(now).is_true() {
                        scan.candidate(due_now);
                    } else {
                        scan.elapsed(window);
                    }
                }
            }
            TrackerStateKind::Idle | TrackerStateKind::Active => {}
        }
        if !tracker.hold_bound_reported()
            && let Some(bound) = tracker.hold_bound()
        {
            if bound.truth(now).is_true() {
                scan.candidate(due_now);
            } else {
                scan.elapsed(bound);
            }
        }
    }
    scan.earliest
        .and_then(|at| on_tick(at, now))
        .filter(|&at| at <= LATEST_HOST_INSTANT)
}

fn takes_margin(body: &Node) -> bool {
    collect_leaves(body)
        .into_iter()
        .any(|leaf| leaf.payload().margined().is_some_and(|m| m.widens()))
}

/// The last instant a host can hold, .NET's `DateTime.MaxValue` (the last
/// tick of 9999): a change past it is reported as none.
const LATEST_HOST_INSTANT: DateTime<Utc> =
    match DateTime::from_timestamp(253_402_300_799, 999_999_900) {
        Some(at) => at,
        None => DateTime::<Utc>::MAX_UTC,
    };

/// `at` floored to the tick grid, or the first tick after `now` when that is
/// not after `now`: a change between two ticks is seen first at the later.
fn on_tick(at: DateTime<Utc>, now: DateTime<Utc>) -> Option<DateTime<Utc>> {
    let past_tick = TimeDelta::nanoseconds(i64::from(at.timestamp_subsec_nanos() % 100));
    let floor = at.checked_sub_signed(past_tick)?;
    if floor > now {
        return Some(floor);
    }
    let now_past_tick = TimeDelta::nanoseconds(i64::from(now.timestamp_subsec_nanos() % 100));
    now.checked_sub_signed(now_past_tick)?
        .checked_add_signed(RESOLUTION)
}

struct Scan<'a> {
    now: DateTime<Utc>,
    rule_id: Uuid,
    glucose_freshness: TimeDelta,
    enabled_at: Option<DateTime<Utc>>,
    ctx: &'a SensorContext,
    timers: &'a TimerStore,
    earliest: Option<DateTime<Utc>>,
}

impl Scan<'_> {
    fn candidate(&mut self, at: Option<DateTime<Utc>>) {
        if let Some(at) = at.filter(|&at| at > self.now) {
            self.earliest = Some(self.earliest.map_or(at, |e| e.min(at)));
        }
    }

    /// Every node counts, whatever its siblings hold: a child's change moves
    /// the leaf log and timers even when the root does not.
    fn tree(&mut self, tree: &Node, root: &str) {
        for (node, path) in nodes_with_paths(tree, root.to_owned()) {
            match node.payload() {
                Payload::Sustained(p) => {
                    if let Some(first) = self.timers.get_first_true(self.rule_id, &path) {
                        self.elapsed(Elapsed::held_for(
                            first,
                            p.minutes.get(),
                            ClockAnchor::Sustained,
                        ));
                    }
                }
                Payload::TimeOfDay(p) => {
                    self.calendar(time_of_day_zone(p, self.ctx), &[p.from, p.to]);
                }
                Payload::DayOfWeek(_) => self.calendar(tenant_zone(self.ctx), &[NaiveTime::MIN]),
                payload => {
                    if let Some(e) = clock_leaf(payload, self.ctx, self.enabled_at, self.now)
                        .and_then(ClockLeaf::elapsed)
                    {
                        self.elapsed(e);
                    }
                    if let Some(e) =
                        freshness_expiry(payload, self.ctx, self.glucose_freshness, self.now)
                    {
                        self.elapsed(e);
                    }
                    if let Some(e) = determination_expiry(payload, self.ctx, self.glucose_freshness)
                    {
                        self.elapsed(e);
                    }
                    if leaf_reads(payload).contains(&ContextFact::SensorState) {
                        self.candidate(self.ctx.sensor_state_lapses(self.now));
                    }
                }
            }
        }
    }

    /// Where the comparison flips, and where an anchor beyond the tolerance
    /// ahead comes within it (§5.4).
    fn elapsed(&mut self, e: Elapsed) {
        self.candidate(e.known_from(self.now));
        for part in e.monotone_parts() {
            if !part.reached(self.now) {
                self.candidate(first_true_after(self.now, |t| part.reached(t)));
            }
        }
    }

    /// The instants the wall clock in `tz` (UTC when `None`) reads one of
    /// `times`, around today: a reading inside a gap is the instant the gap
    /// ends, and a reading inside a fold is both instants and the fold
    /// itself, where the wall clock steps back over it.
    fn calendar(&mut self, tz: Option<Tz>, times: &[NaiveTime]) {
        let today = local(self.now, tz).date();
        let days = [
            today.checked_sub_days(Days::new(1)),
            Some(today),
            today.checked_add_days(Days::new(1)),
            today.checked_add_days(Days::new(2)),
        ];
        for day in days.into_iter().flatten() {
            for &time in times {
                let wall = day.and_time(time);
                match tz {
                    None => self.candidate(Some(wall.and_utc())),
                    Some(tz) => match tz.from_local_datetime(&wall) {
                        LocalResult::Single(at) => self.candidate(Some(at.to_utc())),
                        LocalResult::Ambiguous(a, b) => {
                            let (a, b) = (a.to_utc().min(b.to_utc()), a.to_utc().max(b.to_utc()));
                            self.candidate(Some(a));
                            self.candidate(Some(b));
                            self.candidate(offset_change(tz, a, b));
                        }
                        LocalResult::None => self.candidate(gap_end(tz, wall)),
                    },
                }
            }
        }
    }
}

fn offset_at(tz: Tz, at: DateTime<Utc>) -> FixedOffset {
    tz.offset_from_utc_datetime(&at.naive_utc()).fix()
}

/// The first instant in `(before, after]` whose offset is `after`'s, given
/// one transition between them.
fn offset_change(tz: Tz, before: DateTime<Utc>, after: DateTime<Utc>) -> Option<DateTime<Utc>> {
    let offset = offset_at(tz, after);
    if offset_at(tz, before) == offset {
        return Some(before);
    }
    first_true_between(before, after, |t| offset_at(tz, t) == offset)
}

/// The instant the gap holding `wall` ends: the first wall time after it
/// that exists, traced back to the transition. Gaps are at most a day.
fn gap_end(tz: Tz, wall: NaiveDateTime) -> Option<DateTime<Utc>> {
    let after = (1..=2 * 24 * 60).find_map(|minutes| {
        let later = wall.checked_add_signed(TimeDelta::minutes(minutes))?;
        tz.from_local_datetime(&later).earliest()
    })?;
    let after = after.to_utc();
    offset_change(tz, after.checked_sub_signed(TimeDelta::days(2))?, after)
}

/// The first instant after `now` where `holds`, which is false then true as
/// the instant advances, is true; `None` when it is not true by the end of
/// the representable calendar.
fn first_true_after(
    now: DateTime<Utc>,
    holds: impl Fn(DateTime<Utc>) -> bool,
) -> Option<DateTime<Utc>> {
    let mut before = now;
    let mut step = TimeDelta::seconds(1);
    loop {
        let at = now.checked_add_signed(step)?;
        if holds(at) {
            return first_true_between(before, at, holds);
        }
        before = at;
        step = step.checked_mul(2)?;
    }
}
