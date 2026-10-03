//! Builds the canonical paint-on sequence: an initial wash, pigment dropped
//! in while it is still wet, optional later glazes laid wet-on-dry, then
//! settling and drying.

use crate::domain::{MAX_SETTLE_SHARE, Mask, Operation, Timeline};

/// Fraction of `total_ticks` the implicit settle phase covers when no
/// explicit [`Reveal::settle`] is given, so the wash dries with a visible
/// tail instead of all at once at the end.
pub const DEFAULT_SETTLE_FRACTION: f32 = 0.3;

/// Evaporation rate of the implicit settle phase. Fitted to the drying
/// threshold: a tail at the default fraction settles just above `Dry` rate
/// 1.0 and a slightly longer tail just below it, so a longer tail keeps
/// changing to the last frame instead of drying in the first few ticks and
/// holding a dry frame.
const DEFAULT_SETTLE_RATE: f32 = 1.25;

/// Clamp for the settle evaporation rate, so an extreme tail length cannot
/// drive evaporation to a degenerate value.
const SETTLE_RATE_MIN: f32 = 0.15;
const SETTLE_RATE_MAX: f32 = 64.0;

/// Tail length at which the settle rate is calibrated: a single-wash sheet
/// (the catalogue's `crescent-moon`) finishes drying just before the end of a
/// tail of `SETTLE_TICK_BUDGET` ticks. Water is conserved, so any settle rate
/// eventually dries the sheet; a lower budget only stretches the dry across
/// more of the tail, and longer tails settle slower so the last frames keep
/// changing instead of holding a dry frame.
const SETTLE_TICK_BUDGET: f32 = 40.0;

/// How many e-foldings of film depth a settle is sized to remove.
///
/// A reveal must arrive at dry on its last tick. The timeline ends with an
/// implicit `DryAll`, which settles everything still suspended in one step; if
/// the sheet is still wet when it fires, the artwork visibly jumps to its
/// finished state in the final frame. Before this, `selection-edge` deposited
/// twenty-two times as much pigment on its last tick as it had laid down over
/// the whole tail.
///
/// A rate cannot fix that, because the time a constant evaporation needs is
/// proportional to the water on the sheet and a full-canvas wash carries
/// twenty times what an icon does. Removing a *share* of the remaining film
/// each tick makes the time logarithmic in the water instead, so one number
/// works for every artwork: five e-foldings take the deepest film the
/// simulation allows to well under `dry_threshold`.
const SETTLE_EFOLDINGS: f32 = 2.5;

/// How far the settle damps the base evaporation it sits alongside.
///
/// Once the settle is proportional, the constant sinks are what break the
/// equalisation: they take the same depth per tick whatever is there, so a
/// thin film finishes long before a deep one and the tail dies on exactly the
/// artworks a glaze has already dried. Damping them leaves the share in
/// charge, and the share is logarithmic in the water.
const SETTLE_BASE_DAMP: f32 = 0.25;

/// The share of the remaining film each tick of a settle over `tail_ticks`
/// should take, so the sheet reaches dry as the timeline ends.
pub fn settle_share_for_ticks(tail_ticks: u32) -> f32 {
    (SETTLE_EFOLDINGS / tail_ticks.max(1) as f32).clamp(0.0, MAX_SETTLE_SHARE)
}

/// Replaces any existing settle with one sized for a tail of `tail_ticks`
/// starting at `start`. The `Dry` still sets the base evaporation; the
/// `Settle` is what guarantees the sheet is dry when the timeline ends.
fn push_settle(timeline: &mut Timeline, start: u32, tail_ticks: u32) {
    // Only settles inside the tail: an artwork may place its own earlier, to
    // dry a wash before glazing over it, and that one is not ours to move.
    timeline
        .events
        .retain(|e| !(matches!(e.op, Operation::Settle { .. }) && e.at_tick >= start));
    timeline.push(
        start,
        Operation::Settle {
            share: settle_share_for_ticks(tail_ticks),
        },
    );
}

/// Evaporation rate for a settle tail of `fraction` (fraction of total ticks)
/// from an authored `base_rate`. Scaled inversely with the fraction so the
/// total evaporation over the tail stays roughly constant: a longer tail dries
/// slower, so water keeps leaving and the last soft edges keep firming up to
/// the end instead of the scene holding a dry frame for the tail.
pub fn settle_rate_for(fraction: f32, base_rate: f32) -> f32 {
    let f = fraction.clamp(0.001, 1.0);
    (base_rate * (DEFAULT_SETTLE_FRACTION / f)).clamp(SETTLE_RATE_MIN, SETTLE_RATE_MAX)
}

/// Evaporation rate for a tail of `tail_ticks`: the sheet should still be
/// giving up water at the last frame, so the rate falls as the tail
/// lengthens. `SETTLE_TICK_BUDGET` is the tail length at which a single-wash
/// sheet finishes drying at the end; longer tails settle slower.
pub fn settle_rate_for_ticks(tail_ticks: u32) -> f32 {
    (SETTLE_TICK_BUDGET / tail_ticks.max(1) as f32).clamp(SETTLE_RATE_MIN, SETTLE_RATE_MAX)
}

/// Lengthens the reveal's drying tail so the final `fraction` of ticks settle
/// and dry: the last `Dry { rate }` event is placed at exactly `1 - fraction`
/// of the way through, whether its authored position was before or after that,
/// and a timeline with no settle phase gets one. The tail's evaporation rate
/// comes from its tick count (see [`settle_rate_for_ticks`]) so a longer tail
/// dries slower and keeps changing to the end instead of holding a dry frame.
/// Applied after authoring, so any backend can request it without re-authoring
/// the artwork.
/// Places the reveal's settle where the pen leaves the paper: every tick
/// after the last stroke is the sheet setting into the page, so all of it
/// runs at the settling rate rather than only a trailing fraction of it. A
/// settle placed by tick fraction instead leaves the sheet drying at the
/// base rate for the stretch between the last stroke and the fraction, which
/// is where most of a wash's water goes; the tail then has nothing left to
/// show. Any earlier `Dry` (a glaze boundary) is left alone.
pub fn settle_after_last_stroke(timeline: &mut Timeline) {
    let total = timeline.total_ticks;
    let Some(last_stroke) = timeline
        .events
        .iter()
        .filter(|e| {
            matches!(
                e.op,
                Operation::Brush(_) | Operation::Dab(_) | Operation::Water(_) | Operation::Lift(_)
            )
        })
        .map(|e| e.at_tick)
        .max()
    else {
        return;
    };
    let start = last_stroke.min(total.saturating_sub(1));
    let tail = total.saturating_sub(start);
    let rate = settle_rate_for_ticks(tail) * SETTLE_BASE_DAMP;
    let existing = timeline
        .events
        .iter()
        .rposition(|e| matches!(e.op, Operation::Dry { .. }) && e.at_tick >= last_stroke);
    if let Some(idx) = existing {
        timeline.events.remove(idx);
    }
    timeline.push(start, Operation::Dry { rate });
    push_settle(timeline, start, tail);
}

pub fn apply_settle_fraction(timeline: &mut Timeline, fraction: f32) {
    let f = fraction.clamp(0.0, 1.0);
    if f <= 0.0 {
        return;
    }
    let total = timeline.total_ticks;
    let start = ((1.0 - f) * total as f32).round() as u32;
    let tail = total.saturating_sub(start);
    let rate = settle_rate_for_ticks(tail) * SETTLE_BASE_DAMP;
    let last_dry = timeline
        .events
        .iter()
        .rposition(|e| matches!(e.op, Operation::Dry { .. }));
    match last_dry {
        Some(idx) if timeline.events[idx].at_tick != start => {
            timeline.events.remove(idx);
            timeline.push(start, Operation::Dry { rate });
        }
        Some(_) => {}
        None => {
            timeline.push(start, Operation::Dry { rate });
        }
    }
    push_settle(timeline, start, tail);
}

#[derive(Debug, Clone)]
pub struct Reveal {
    total_ticks: u32,
    mask: Option<Mask>,
    wash: Vec<Operation>,
    drops: Vec<(f32, Operation)>,
    glazes: Vec<(f32, Operation)>,
    settle: Option<(f32, f32)>,
    settle_fraction: f32,
}

impl Reveal {
    pub fn new(total_ticks: u32) -> Reveal {
        Reveal {
            total_ticks: total_ticks.max(1),
            mask: None,
            wash: Vec::new(),
            drops: Vec::new(),
            glazes: Vec::new(),
            settle: None,
            settle_fraction: DEFAULT_SETTLE_FRACTION,
        }
    }

    /// Restricts where paint may go for the whole reveal.
    pub fn mask(mut self, mask: Mask) -> Self {
        self.mask = Some(mask);
        self
    }

    /// Phase 1: applied at tick 0.
    pub fn wash(mut self, op: Operation) -> Self {
        self.wash.push(op);
        self
    }

    /// Phase 2: applied at `at` (fraction of total ticks) while the wash is
    /// still wet, so it spreads and accumulates.
    pub fn drop_in(mut self, at: f32, op: Operation) -> Self {
        self.drops.push((at.clamp(0.0, 1.0), op));
        self
    }

    /// A later glaze laid wet-on-dry: everything painted so far is dried
    /// (`DryAll`) at the first glaze tick, then `op` is applied, so the new
    /// edge stays crisp and the overlap mixes optically rather than in water.
    pub fn glaze(mut self, at: f32, op: Operation) -> Self {
        self.glazes.push((at.clamp(0.0, 1.0), op));
        self
    }

    /// Phase 3: from `from` (fraction of total ticks) evaporation runs at
    /// `rate` times the base rate so the wash settles and dries. Beats the
    /// implicit [`Reveal::settle_fraction`] tail.
    pub fn settle(mut self, from: f32, rate: f32) -> Self {
        self.settle = Some((from.clamp(0.0, 1.0), rate));
        self
    }

    /// Fraction of the total ticks spent settling and drying when no
    /// [`Reveal::settle`] is given. The implicit settle starts at
    /// `1 - fraction`; the default leaves the last 30 % drying.
    pub fn settle_fraction(mut self, fraction: f32) -> Self {
        self.settle_fraction = fraction.clamp(0.0, 1.0);
        self
    }

    pub fn build(self) -> Timeline {
        let total = self.total_ticks;
        let at = |f: f32| ((f * total as f32).round() as u32).min(total.saturating_sub(1));
        let mut timeline = Timeline::new(total);
        if let Some(mask) = self.mask {
            timeline.push(0, Operation::SetMask(mask));
        }
        for op in self.wash {
            timeline.push(0, op);
        }
        for (f, op) in self.drops {
            timeline.push(at(f), op);
        }
        let mut dried_at: Option<u32> = None;
        for (f, op) in self.glazes {
            let tick = at(f);
            if dried_at.is_none_or(|t| t != tick) && dried_at.is_none() {
                timeline.push(tick, Operation::DryAll);
                dried_at = Some(tick);
            }
            timeline.push(tick, op);
        }
        match self.settle {
            Some((from, rate)) => {
                timeline.push(at(from), Operation::Dry { rate });
            }
            None => {
                let start = at(1.0 - self.settle_fraction);
                timeline.push(
                    start,
                    Operation::Dry {
                        rate: settle_rate_for(self.settle_fraction, DEFAULT_SETTLE_RATE),
                    },
                );
            }
        }
        timeline.push(total, Operation::DryAll);
        timeline
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn phases_land_in_order() {
        let t = Reveal::new(200)
            .wash(Operation::ClearMask)
            .drop_in(0.25, Operation::ClearMask)
            .settle(0.6, 3.0)
            .build();
        let ticks: Vec<u32> = t.events.iter().map(|e| e.at_tick).collect();
        assert_eq!(ticks, vec![0, 50, 120, 200]);
        assert_eq!(t.events.last().unwrap().op, Operation::DryAll);
    }

    #[test]
    fn glazes_dry_everything_first_then_apply() {
        let t = Reveal::new(100)
            .wash(Operation::ClearMask)
            .glaze(0.5, Operation::Dry { rate: 2.0 })
            .glaze(0.5, Operation::ClearMask)
            .build();
        let at_50: Vec<&Operation> = t
            .events
            .iter()
            .filter(|e| e.at_tick == 50)
            .map(|e| &e.op)
            .collect();
        assert_eq!(at_50[0], &Operation::DryAll);
        assert_eq!(at_50.len(), 3);
    }

    fn dry_ticks(t: &Timeline) -> Vec<u32> {
        t.events
            .iter()
            .filter_map(|e| match e.op {
                Operation::Dry { .. } => Some(e.at_tick),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn implicit_settle_covers_the_last_third_of_the_ticks() {
        let t = Reveal::new(200).wash(Operation::ClearMask).build();
        assert_eq!(dry_ticks(&t), vec![140]);
        assert_eq!(t.events.last().unwrap().op, Operation::DryAll);
    }

    #[test]
    fn settle_fraction_moves_the_implicit_tail() {
        let t = Reveal::new(200)
            .wash(Operation::ClearMask)
            .settle_fraction(0.1)
            .build();
        assert_eq!(dry_ticks(&t), vec![180]);
    }

    #[test]
    fn explicit_settle_beats_the_implicit_tail() {
        let t = Reveal::new(200)
            .wash(Operation::ClearMask)
            .settle(0.85, 2.0)
            .build();
        assert_eq!(dry_ticks(&t), vec![170]);
    }

    #[test]
    fn settle_rate_is_unity_at_the_default_fraction_and_falls_inversely() {
        assert!((settle_rate_for(DEFAULT_SETTLE_FRACTION, 3.0) - 3.0).abs() < 1e-6);
        let slower = settle_rate_for(0.4, 3.0);
        let slowest = settle_rate_for(0.6, 3.0);
        assert!(slower < 3.0 && slower > 2.0, "slower {slower}");
        assert!(slowest < slower, "slowest {slowest}");
        assert!((settle_rate_for(0.4, 3.0) - 3.0 * DEFAULT_SETTLE_FRACTION / 0.4).abs() < 1e-6);
        assert_eq!(settle_rate_for(0.001, 3.0), SETTLE_RATE_MAX);
        assert_eq!(settle_rate_for(1.0, 0.1), SETTLE_RATE_MIN);
    }

    fn last_dry_tick(timeline: &Timeline) -> u32 {
        timeline
            .events
            .iter()
            .filter(|e| matches!(e.op, Operation::Dry { .. }))
            .map(|e| e.at_tick)
            .max()
            .unwrap_or(0)
    }

    fn tail_dry_rate(timeline: &Timeline) -> f32 {
        timeline
            .events
            .iter()
            .filter_map(|e| match e.op {
                Operation::Dry { rate } => Some((e.at_tick, rate)),
                _ => None,
            })
            .max_by_key(|(t, _)| *t)
            .map(|(_, rate)| rate)
            .unwrap_or(0.0)
    }

    #[test]
    fn settle_fraction_relocates_an_existing_tail_and_inserts_when_missing() {
        let mut t = Reveal::new(200)
            .wash(Operation::ClearMask)
            .settle(0.55, 3.5)
            .build();
        let total = t.total_ticks;
        apply_settle_fraction(&mut t, 0.5);
        assert_eq!(
            last_dry_tick(&t),
            ((1.0 - 0.5) * total as f32).round() as u32,
            "relocating the authored settle to the requested tail start"
        );
        assert!(
            (tail_dry_rate(&t)
                - settle_rate_for_ticks(total - (total as f32 * 0.5).round() as u32)
                    * SETTLE_BASE_DAMP)
                .abs()
                < 1e-4,
            "relocation rates the tail by its tick count, damped for the settle share"
        );

        let mut bare = Reveal::new(200).wash(Operation::ClearMask).build();
        bare.events
            .retain(|e| !matches!(e.op, Operation::Dry { .. }));
        apply_settle_fraction(&mut bare, 0.5);
        assert_eq!(
            last_dry_tick(&bare),
            ((1.0 - 0.5) * bare.total_ticks as f32).round() as u32
        );
        assert!(
            (tail_dry_rate(&bare) - settle_rate_for_ticks(100) * SETTLE_BASE_DAMP).abs() < 1e-4,
            "the inserted settle rates the tail by its tick count, damped"
        );

        let mut untouched = Reveal::new(200).wash(Operation::ClearMask).build();
        apply_settle_fraction(&mut untouched, 0.0);
        assert_eq!(
            untouched,
            Reveal::new(200).wash(Operation::ClearMask).build()
        );
    }

    /// The settle that guarantees a reveal reaches dry: its share falls as
    /// the tail lengthens, so the sheet takes the whole tail to dry however
    /// long that is, and it is always emitted alongside the `Dry`.
    #[test]
    fn a_settle_is_placed_with_every_tail_and_scales_with_it() {
        let share_of = |t: &Timeline| {
            t.events.iter().rev().find_map(|e| match e.op {
                Operation::Settle { share } => Some(share),
                _ => None,
            })
        };
        let mut short = Reveal::new(200).wash(Operation::ClearMask).build();
        apply_settle_fraction(&mut short, 0.25);
        let mut long = Reveal::new(200).wash(Operation::ClearMask).build();
        apply_settle_fraction(&mut long, 0.75);
        let (s, l) = (share_of(&short).unwrap(), share_of(&long).unwrap());
        assert!(
            s > l,
            "a shorter tail has to take more each tick: {s} vs {l}"
        );
        assert!(
            (s * 50.0 - l * 150.0).abs() < 1e-3,
            "both remove the same e-foldings"
        );

        let mut once = Reveal::new(200).wash(Operation::ClearMask).build();
        apply_settle_fraction(&mut once, 0.5);
        apply_settle_fraction(&mut once, 0.5);
        assert_eq!(
            once.events
                .iter()
                .filter(|e| matches!(e.op, Operation::Settle { .. }))
                .count(),
            1,
            "re-applying a settle replaces it rather than stacking another"
        );
    }

    #[test]
    fn settle_rate_falls_as_the_tail_lengthens() {
        assert_eq!(settle_rate_for_ticks(SETTLE_TICK_BUDGET as u32), 1.0);
        assert!(settle_rate_for_ticks(105) > settle_rate_for_ticks(210));
        assert!(settle_rate_for_ticks(210) > settle_rate_for_ticks(420));
        assert_eq!(
            settle_rate_for_ticks(1),
            SETTLE_TICK_BUDGET,
            "a hair of tail clamps to the budget"
        );
        assert_eq!(settle_rate_for_ticks(0), SETTLE_TICK_BUDGET);
        assert_eq!(
            settle_rate_for_ticks(u32::MAX),
            SETTLE_RATE_MIN,
            "an endless tail clamps low"
        );
    }

    #[test]
    fn the_settle_is_placed_at_the_tail_start_from_either_side() {
        let mut early = Reveal::new(200)
            .wash(Operation::ClearMask)
            .settle(0.5, 3.0)
            .build();
        apply_settle_fraction(&mut early, 0.45);
        assert_eq!(
            last_dry_tick(&early),
            ((1.0 - 0.45) * 200.0f32).round() as u32,
            "an authored settle at 0.5 moves later to the 0.45 tail start"
        );

        let mut late = Reveal::new(200)
            .wash(Operation::ClearMask)
            .settle(0.9, 3.0)
            .build();
        apply_settle_fraction(&mut late, 0.45);
        assert_eq!(
            last_dry_tick(&late),
            ((1.0 - 0.45) * 200.0f32).round() as u32,
            "an authored settle at 0.9 moves earlier to the 0.45 tail start"
        );
    }
}
