//! Rewrites a built timeline so every stroke is drawn instead of stamped
//! whole: the mark grows along its own path, at the authored radius, over the
//! ticks its window gives it. A stroke's spans tile `0..1` and carry the
//! authored radius, pigment, water and softness unchanged, so `choreograph`
//! is a pure re-timing: the union of everything emitted is the authored stamp,
//! and the finished artwork is the one that was authored.
//!
//! That is not a nicety. The artworks are stencil-and-fill drawings whose
//! silhouette lives in a `SetMask`, and a fill stroke's only job is to deliver
//! pigment everywhere inside it. Laying such a stroke with a narrowed tip
//! leaves the stencil unfilled and the object unrecognisable, which is what an
//! earlier tip-and-halo version of this pass did to `alarm-bell` and
//! `report-pages`. A stroke that arrives too fast is a pacing problem and is
//! fixed by giving the pen a longer path to walk (see `authoring::hatch`);
//! it is never fixed by making the brush smaller.
//!
//! The pen paces itself by what it is drawing. [`Choreography::paint_spread`]
//! sizes the paint budget — that share of the phase up to the settle's tick —
//! and each stroke's window is its share of the budget by weight (path length
//! plus widest radius), so a long wash takes longer to draw than a dab and
//! the pen moves at a roughly even speed. Only the *order* of the strokes
//! carries over from the authored timeline, not their spacing: strokes are
//! drawn one after another wherever they were written, and a non-stroke op
//! (`DryAll`, `SetMask`, `ClearMask`, `Dry`) between them lands where the pen
//! has reached, so a glaze still dries what came before it.
//!
//! `choreograph` never lets the sequence cross the paint phase without making
//! room: where the strokes need more ticks than the budget allows, every
//! later event shifts (and `total_ticks` grows) by the overrun, bounded by
//! [`Choreography::max_stretch`], so authored artworks need no editing.

use crate::domain::ops::{BrushStroke, LiftStroke, Operation, StrokeSpan, WaterStroke};
use crate::domain::scene::{MAX_CONCENTRATION, MAX_WATER};
use crate::domain::{Point, Timeline};

/// How a stroke is drawn. `paint_spread` and `stroke_overlap` decide where
/// the laydowns land, `max_steps` how finely each is cut, and `max_stretch`
/// bounds how far the whole timeline may grow.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Choreography {
    /// Share of the paint phase (`paint_end`, the settle's tick) the pen is
    /// still laying paint in; the budget the sequence spans. The rest of the
    /// phase is the spread that follows the last stroke, so a reveal reads as
    /// drawing rather than as an instant that then blooms.
    pub paint_spread: f32,
    /// Upper bound on the laydown steps a stroke is cut into; a stroke with a
    /// shorter window than this is cut into one span per tick, so no step is
    /// ever emitted onto a tick that already has one.
    pub max_steps: u32,
    /// Share of a stroke's laydown the next stroke starts within, so the pen
    /// flows from one mark to the next instead of pausing between them.
    pub stroke_overlap: f32,
    /// Upper bound on `total_ticks` growth, as a multiple.
    pub max_stretch: f32,
}

impl Default for Choreography {
    fn default() -> Self {
        Choreography {
            paint_spread: 0.6,
            max_steps: 12,
            stroke_overlap: 0.15,
            max_stretch: 2.5,
        }
    }
}

fn is_stroke(op: &Operation) -> bool {
    matches!(
        op,
        Operation::Brush(_) | Operation::Dab(_) | Operation::Water(_) | Operation::Lift(_)
    )
}

fn path_length(path: &[Point]) -> f32 {
    path.windows(2)
        .map(|w| ((w[1].x - w[0].x).powi(2) + (w[1].y - w[0].y).powi(2)).sqrt())
        .sum()
}

/// The pen's cost of a stroke: its path length plus its widest radius, so a
/// dab is short but not zero and a long sweep takes proportionally longer.
fn stroke_weight(op: &Operation) -> f32 {
    match op {
        Operation::Brush(s) => path_length(&s.path) + s.radius.max(),
        Operation::Dab(s) => s.radius,
        Operation::Water(s) => path_length(&s.path) + s.radius.max(),
        Operation::Lift(s) => path_length(&s.path) + s.radius.max(),
        _ => 0.0,
    }
}

/// The paint phase's end: the settle's tick, else the last stroke's tick,
/// else `total_ticks`. Strokes at or before it are drawn in sequence within
/// the budget `paint_spread * paint_end`; everything after it keeps its
/// authored tick.
///
/// The settle is the *last* `Dry`, not the first: a glaze boundary emits a
/// `Dry` of its own to return evaporation to the base rate, so the first one
/// is usually the start of the second wash rather than the end of the
/// painting. Reading it as the settle collapses the budget to whatever
/// precedes the first glaze and leaves every later stroke outside the
/// sequence, at its authored tick.
fn paint_end_of(timeline: &Timeline) -> u32 {
    timeline
        .events
        .iter()
        .rev()
        .find(|e| matches!(e.op, Operation::Dry { .. }))
        .map(|e| e.at_tick)
        .or_else(|| {
            timeline
                .events
                .iter()
                .rev()
                .find(|e| is_stroke(&e.op))
                .map(|e| e.at_tick)
        })
        .unwrap_or(timeline.total_ticks)
}

/// The narrowest laydown window a stroke may be given, so even a dab that
/// weighs nothing against a long sweep still arrives over more than one tick.
const MIN_WINDOW: u32 = 2;

/// One stroke's share of the paint budget, by weight.
fn laydown_window(_params: &Choreography, budget: u32, weight: f32, total_weight: f32) -> u32 {
    let share = if total_weight > 0.0 {
        (budget as f32 * weight / total_weight).round() as u32
    } else {
        0
    };
    share.max(MIN_WINDOW)
}

/// How many spans a stroke with this window is cut into. More steps than the
/// window has ticks would put two spans on one tick and buy nothing, so the
/// window is the ceiling and [`Choreography::max_steps`] the cap.
fn steps_for(params: &Choreography, window: u32) -> u32 {
    window.min(params.max_steps).max(1)
}

/// How far the pen moves on to the next stroke after a stroke of `window`.
fn stroke_advance(params: &Choreography, window: u32) -> u32 {
    (window as f32 * (1.0 - params.stroke_overlap)).round() as u32
}

/// The sequence's cursor: where the next stroke starts, and how far the
/// furthest emitted op has reached. Shared by the projection and the build so
/// the make-room shift is measured on the same walk that emits the ops.
#[derive(Debug, Clone, Copy, Default)]
struct SequenceWalk {
    cursor: u32,
    footprint: u32,
}

impl SequenceWalk {
    fn stroke(&mut self, params: &Choreography, window: u32) {
        let steps = steps_for(params, window);
        // The furthest any stroke has reached, not the furthest this one did.
        // Strokes overlap, so a dab drawn after a long sweep finishes earlier
        // than the sweep does; assigning here instead of taking the maximum
        // would walk the boundary backwards, and the `ClearMask` that follows
        // would drop the stencil while the sweep was still being laid.
        let reach = self.cursor + laydown_offset(window, steps, steps - 1) + 1;
        self.footprint = self.footprint.max(reach);
        self.cursor += stroke_advance(params, window);
    }

    /// A non-stroke op lands where the pen has reached: every stroke before
    /// it is finished first, so a glaze dries — and a `ClearMask` releases —
    /// only what has actually been laid.
    fn boundary(&mut self) -> u32 {
        self.cursor = self.cursor.max(self.footprint);
        self.cursor
    }

    fn end(&self) -> u32 {
        self.footprint.max(self.cursor)
    }
}

/// How far past `paint_end` the sequence extends, without building the
/// output, so [`choreograph`] can size the make-room shift and the stretch
/// cap on tick arithmetic alone.
fn sequence_footprint(
    timeline: &Timeline,
    params: &Choreography,
    budget: u32,
    paint_end: u32,
) -> u32 {
    let total_weight: f32 = timeline
        .events
        .iter()
        .filter(|e| e.at_tick <= paint_end)
        .map(|e| stroke_weight(&e.op))
        .sum();
    let mut walk = SequenceWalk::default();
    for e in &timeline.events {
        if e.at_tick > paint_end {
            break;
        }
        if is_stroke(&e.op) {
            let window = laydown_window(params, budget, stroke_weight(&e.op), total_weight);
            walk.stroke(params, window);
        } else {
            walk.boundary();
        }
    }
    walk.end()
}

/// Re-times `timeline` so the pen draws every stroke at or before `paint_end`
/// as a sequence paced by path length within the paint budget, with the
/// bloom halos trailing [`Choreography::bloom_trail`] of each stroke's window
/// behind it. A non-stroke op at or before `paint_end` keeps its place in the
/// order at the cursor; events after it keep their source ticks. When the
/// sequence needs more ticks than the phase allows, later events shift (and
/// `total_ticks` grows) by the overrun, bounded by `max_stretch`.
pub fn choreograph(timeline: &Timeline, params: &Choreography) -> Timeline {
    let params = *params;
    let source_total = timeline.total_ticks;
    let paint_end = paint_end_of(timeline);
    if !timeline
        .events
        .iter()
        .any(|e| e.at_tick <= paint_end && is_stroke(&e.op))
    {
        return timeline.clone();
    }
    let mut budget = (params.paint_spread * paint_end as f32).round() as u32;

    let mut over =
        sequence_footprint(timeline, &params, budget, paint_end).saturating_sub(paint_end);
    if over > 0 && (source_total as f32 + over as f32) > params.max_stretch * source_total as f32 {
        // The sequence would grow the timeline past `max_stretch`; scale the
        // budget so it just fits. The per-stroke window's floor can leave a
        // residue, which is accepted: the cap bounds the growth, it does not
        // hit it exactly.
        let fit = ((params.max_stretch - 1.0) * source_total as f32 / over as f32).max(0.0);
        budget = (budget as f32 * fit).round() as u32;
        over = sequence_footprint(timeline, &params, budget, paint_end).saturating_sub(paint_end);
    }
    build_sequence(timeline, &params, budget, paint_end, over)
}

fn build_sequence(
    timeline: &Timeline,
    params: &Choreography,
    budget: u32,
    paint_end: u32,
    make_room: u32,
) -> Timeline {
    let total = timeline.total_ticks.saturating_add(make_room);
    let total_weight: f32 = timeline
        .events
        .iter()
        .filter(|e| e.at_tick <= paint_end)
        .map(|e| stroke_weight(&e.op))
        .sum();
    let mut out = Timeline::new(total);
    let mut walk = SequenceWalk::default();
    for e in &timeline.events {
        if e.at_tick > paint_end {
            push_clamped(
                &mut out,
                e.at_tick.saturating_add(make_room),
                e.op.clone(),
                total,
            );
            continue;
        }
        if !is_stroke(&e.op) {
            push_clamped(&mut out, walk.boundary(), e.op.clone(), total);
            continue;
        }
        let window = laydown_window(params, budget, stroke_weight(&e.op), total_weight);
        if matches!(e.op, Operation::Dab(_)) {
            push_clamped(&mut out, walk.cursor, e.op.clone(), total);
            walk.stroke(params, window);
            continue;
        }
        let steps = steps_for(params, window);
        let start = walk.cursor;
        for step in 0..steps {
            let tick = start + laydown_offset(window, steps, step);
            let span = span_for(steps, step);
            let op = match &e.op {
                Operation::Brush(s) => Operation::Brush(BrushStroke {
                    path: s.path.clone(),
                    radius: s.radius,
                    pigment: s.pigment,
                    concentration: s.concentration.clamp(0.0, MAX_CONCENTRATION),
                    water: s.water.clamp(0.0, MAX_WATER),
                    softness: s.softness,
                    span,
                }),
                Operation::Water(w) => Operation::Water(WaterStroke {
                    path: w.path.clone(),
                    radius: w.radius,
                    water: w.water,
                    softness: w.softness,
                    span,
                }),
                Operation::Lift(l) => Operation::Lift(LiftStroke {
                    path: l.path.clone(),
                    radius: l.radius,
                    strength: l.strength,
                    softness: l.softness,
                    span,
                }),
                _ => unreachable!("strokes only"),
            };
            push_clamped(&mut out, tick, op, total);
        }
        walk.stroke(params, window);
    }
    out
}

fn laydown_offset(laydown: u32, steps: u32, step: u32) -> u32 {
    (step as f32 * laydown as f32 / steps as f32).round() as u32
}

fn span_for(steps: u32, step: u32) -> StrokeSpan {
    StrokeSpan::new(step as f32 / steps as f32, (step + 1) as f32 / steps as f32)
}

fn push_clamped(out: &mut Timeline, tick: u32, op: Operation, total: u32) {
    out.push(tick.min(total), op);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::ops::RadiusProfile;
    use crate::domain::{Mask, Point};

    const PARAMS: Choreography = Choreography {
        paint_spread: 0.6,
        max_steps: 12,
        stroke_overlap: 0.15,
        max_stretch: 2.5,
    };

    /// The spans one stroke of `weight` is cut into, given the budget the
    /// whole sequence shares.
    fn steps(budget: u32, weight: f32, total_weight: f32) -> usize {
        steps_for(
            &PARAMS,
            laydown_window(&PARAMS, budget, weight, total_weight),
        ) as usize
    }

    /// A mark small enough that its laydown window hits the floor, which is
    /// where a boundary that tracked only the last stroke went wrong.
    fn dab_brush(path: Vec<Point>) -> Operation {
        Operation::Brush(BrushStroke {
            path,
            radius: RadiusProfile::uniform(0.02),
            pigment: 0,
            concentration: 0.5,
            water: 0.8,
            softness: 0.3,
            span: StrokeSpan::FULL,
        })
    }

    fn brush(path: Vec<Point>, concentration: f32, water: f32) -> Operation {
        Operation::Brush(BrushStroke {
            path,
            radius: RadiusProfile::uniform(0.1),
            pigment: 0,
            concentration,
            water,
            softness: 0.3,
            span: StrokeSpan::FULL,
        })
    }

    fn line(a: Point, b: Point) -> Vec<Point> {
        vec![a, b]
    }

    fn brushes(timeline: &Timeline) -> Vec<(u32, &BrushStroke)> {
        timeline
            .events
            .iter()
            .filter_map(|e| match &e.op {
                Operation::Brush(s) => Some((e.at_tick, s)),
                _ => None,
            })
            .collect()
    }

    fn waters(timeline: &Timeline) -> Vec<(u32, &WaterStroke)> {
        timeline
            .events
            .iter()
            .filter_map(|e| match &e.op {
                Operation::Water(s) => Some((e.at_tick, s)),
                _ => None,
            })
            .collect()
    }

    /// Ticks the first laydown step of every stroke (span start 0.0), in
    /// source order. The pen starts each stroke here, so ordering the strokes
    /// by these ticks is ordering them by when the pen reaches them. Only
    /// `Brush` and `Lift` are consulted, so an authored `Water` stroke does
    /// not count as a start.
    fn stroke_starts(timeline: &Timeline) -> Vec<u32> {
        timeline
            .events
            .iter()
            .filter(|e| {
                let span = match &e.op {
                    Operation::Brush(s) => Some(s.span),
                    Operation::Lift(l) => Some(l.span),
                    _ => None,
                };
                span.is_some_and(|s| s.start <= 0.0)
            })
            .map(|e| e.at_tick)
            .collect()
    }

    #[test]
    fn a_stroke_becomes_a_walk_along_its_path() {
        let path = line(Point::new(0.2, 0.5), Point::new(0.8, 0.5));
        let mut src = Timeline::new(200);
        src.push(0, brush(path, 0.5, 0.8));
        src.push(150, Operation::Dry { rate: 3.0 });
        let out = choreograph(&src, &PARAMS);

        let laid = brushes(&out);
        let n = steps((PARAMS.paint_spread * 150.0).round() as u32, 0.7, 0.7);
        assert_eq!(laid.len(), n);
        assert!(waters(&out).is_empty(), "no water is invented");

        let ticks: Vec<u32> = laid.iter().map(|(t, _)| *t).collect();
        let mut ascending = ticks.clone();
        ascending.sort_unstable();
        assert_eq!(ticks, ascending, "laydown ticks ascend");

        for (i, (_, s)) in laid.iter().enumerate() {
            let expected = StrokeSpan::new(i as f32 / n as f32, (i + 1) as f32 / n as f32);
            assert_eq!(s.span, expected, "step {i}");
            if i > 0 {
                assert_eq!(s.span.start, laid[i - 1].1.span.end, "contiguous");
            }
        }
        assert_eq!(laid.first().unwrap().1.span.start, 0.0);
        assert_eq!(laid.last().unwrap().1.span.end, 1.0);

        assert_eq!(out.total_ticks, 200, "one stroke fits without stretching");
    }

    /// Every laydown carries the authored amounts and the authored radius:
    /// the spans partition the coverage, so scaling any of them would change
    /// what the finished artwork holds. See the module doc.
    #[test]
    fn every_laydown_carries_the_authored_stroke() {
        let (conc, water) = (0.5f32, 0.8f32);
        let authored = RadiusProfile::uniform(0.1);
        let mut src = Timeline::new(200);
        src.push(
            0,
            brush(
                line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)),
                conc,
                water,
            ),
        );
        let out = choreograph(&src, &PARAMS);

        let laid = brushes(&out);
        assert!(!laid.is_empty());
        assert!(waters(&out).is_empty(), "no water is invented");
        for (i, (_, s)) in laid.iter().enumerate() {
            assert_eq!(s.concentration, conc, "step {i} pigment");
            assert_eq!(s.water, water, "step {i} water");
            assert_eq!(s.radius, authored, "step {i} radius");
            assert_eq!(s.softness, 0.3, "step {i} softness");
        }
    }

    #[test]
    fn strokes_are_drawn_one_after_another() {
        let mut src = Timeline::new(150);
        for _ in 0..3 {
            src.push(
                0,
                brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
            );
        }
        src.push(100, Operation::Dry { rate: 3.0 });
        let out = choreograph(&src, &PARAMS);

        let budget = (PARAMS.paint_spread * 100.0).round() as u32;
        let window = laydown_window(&PARAMS, budget, 0.7, 3.0 * 0.7);
        let advance = stroke_advance(&PARAMS, window);
        let starts = stroke_starts(&out);
        assert_eq!(starts, vec![0, advance, 2 * advance], "one after another");
        assert!(
            starts.windows(2).all(|w| w[0] < w[1]),
            "laydown starts are distinct and ascending"
        );
        let last = *brushes(&out).iter().map(|(t, _)| t).max().unwrap();
        assert!(
            last <= budget,
            "the last laydown lands within the paint budget ({last} <= {budget})"
        );
    }

    #[test]
    fn a_long_stroke_takes_longer_than_a_dab() {
        let long = line(Point::new(0.05, 0.5), Point::new(0.95, 0.5));
        let dab = vec![Point::new(0.5, 0.5)];
        let mut src = Timeline::new(200);
        src.push(0, brush(long.clone(), 0.5, 0.8));
        src.push(10, brush(dab.clone(), 0.5, 0.8));
        src.push(100, Operation::Dry { rate: 3.0 });
        let out = choreograph(&src, &PARAMS);

        let span = |path: &[Point]| {
            let ticks: Vec<u32> = brushes(&out)
                .iter()
                .filter(|(_, s)| s.path == path)
                .map(|(t, _)| *t)
                .collect();
            ticks.last().unwrap() - ticks.first().unwrap()
        };
        assert!(
            span(&long) > span(&dab),
            "the sweep's laydown {} spans more than the dab's {}",
            span(&long),
            span(&dab)
        );
    }

    #[test]
    fn the_sequence_fits_the_paint_budget() {
        let paint_end = 200u32;
        let budget = (PARAMS.paint_spread * paint_end as f32).round() as u32;

        let mut many = Timeline::new(400);
        for t in [0u32, 10, 20, 30, 40, 50, 60, 70] {
            many.push(
                t,
                brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
            );
        }
        many.push(paint_end, Operation::Dry { rate: 3.0 });
        let out = choreograph(&many, &PARAMS);
        let last = *brushes(&out).iter().map(|(t, _)| t).max().unwrap();
        assert!(
            last <= budget,
            "many strokes: last laydown at {last} lands within the budget {budget}"
        );

        let mut one = Timeline::new(400);
        one.push(
            0,
            brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
        );
        one.push(paint_end, Operation::Dry { rate: 3.0 });
        let out = choreograph(&one, &PARAMS);
        let last = *brushes(&out).iter().map(|(t, _)| t).max().unwrap();
        assert!(
            last <= budget,
            "single stroke: last laydown at {last} lands within the budget {budget}"
        );
    }

    /// A wide fill is hatched into one long path, and the marks that follow
    /// it are short. If the boundary tracked only the last stroke it would
    /// land before the long one had finished, and the `ClearMask` after it
    /// would release the stencil mid-laydown — which put a bar of unclipped
    /// paint across the bottom of `heart`.
    #[test]
    fn a_boundary_waits_for_the_longest_stroke_before_it() {
        // Eight rows across the frame, as `Frame::hatch` lays a fill, then a
        // mark a fiftieth of its length.
        let hatch: Vec<Point> = (0..8)
            .flat_map(|i| {
                let y = 0.1 + i as f32 * 0.1;
                let (a, b) = if i % 2 == 0 {
                    (0.02, 0.98)
                } else {
                    (0.98, 0.02)
                };
                [Point::new(a, y), Point::new(b, y)]
            })
            .collect();
        let dab = vec![Point::new(0.5, 0.2)];
        let mut src = Timeline::new(300);
        src.push(0, brush(hatch, 0.5, 0.8));
        src.push(20, dab_brush(dab.clone()));
        src.push(40, Operation::ClearMask);
        src.push(200, Operation::Dry { rate: 3.0 });
        let out = choreograph(&src, &PARAMS);

        let clear = out
            .events
            .iter()
            .find(|e| e.op == Operation::ClearMask)
            .expect("ClearMask kept")
            .at_tick;
        let last_paint = out
            .events
            .iter()
            .filter(|e| is_stroke(&e.op))
            .map(|e| e.at_tick)
            .max()
            .unwrap();
        assert!(
            clear >= last_paint,
            "the stencil was released at {clear}, before the last laydown at {last_paint}"
        );
    }

    #[test]
    fn glaze_boundaries_keep_their_place_in_the_order() {
        let first_path = line(Point::new(0.1, 0.5), Point::new(0.9, 0.5));
        let second_path = line(Point::new(0.2, 0.5), Point::new(0.3, 0.5));
        let mut src = Timeline::new(100);
        src.push(0, brush(first_path.clone(), 0.5, 0.8));
        src.push(20, Operation::DryAll);
        src.push(20, brush(second_path.clone(), 0.6, 0.9));
        let out = choreograph(&src, &PARAMS);

        let dry = out
            .events
            .iter()
            .position(|e| e.op == Operation::DryAll)
            .expect("DryAll kept");
        for (i, e) in out.events.iter().enumerate() {
            let from_first = matches!(&e.op, Operation::Brush(s) if s.path == first_path);
            let from_second = matches!(&e.op, Operation::Brush(s) if s.path == second_path);
            assert!(!from_first || i < dry, "first-stroke op after the DryAll");
            assert!(
                !from_second || i > dry,
                "second-stroke op before the DryAll"
            );
        }
    }

    #[test]
    fn the_timeline_grows_only_by_the_sequence_overrun() {
        let mut fits = Timeline::new(120);
        fits.push(
            10,
            brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
        );
        fits.push(100, Operation::Dry { rate: 3.0 });
        let out = choreograph(&fits, &PARAMS);
        assert_eq!(
            out.total_ticks, 120,
            "a sequence inside the phase grows nothing"
        );

        let mut tight = Timeline::new(10);
        tight.push(
            0,
            brush(line(Point::new(0.1, 0.5), Point::new(0.9, 0.5)), 0.5, 0.8),
        );
        tight.push(5, Operation::DryAll);
        let over = sequence_footprint(&tight, &PARAMS, 0, 0);
        let out = choreograph(&tight, &PARAMS);
        assert_eq!(
            out.total_ticks,
            tight.total_ticks.saturating_add(over),
            "the timeline grows only by the sequence overrun"
        );
        let dry_all = out
            .events
            .iter()
            .position(|e| e.op == Operation::DryAll)
            .expect("DryAll kept");
        assert!(
            out.events
                .iter()
                .take(dry_all)
                .all(|e| matches!(&e.op, Operation::Brush(_) | Operation::Water(_))),
            "the DryAll still follows the stroke's whole laydown"
        );
    }

    #[test]
    fn the_stretch_cap_holds() {
        // A long last stroke dominates the budget, so the sequence overruns
        // the paint phase; the cap scales the budget down until it just fits.
        let mut src = Timeline::new(100);
        src.push(0, brush(vec![Point::new(0.5, 0.5)], 0.5, 0.8));
        src.push(5, brush(vec![Point::new(0.5, 0.5)], 0.5, 0.8));
        src.push(
            10,
            brush(line(Point::new(0.0, 0.5), Point::new(1.0, 0.5)), 0.5, 0.8),
        );
        src.push(90, Operation::Dry { rate: 3.0 });
        // A spread past 1.0 is what makes the sequence outrun its phase: the
        // windows sum to more ticks than the phase holds.
        let params = Choreography {
            paint_spread: 1.5,
            max_stretch: 1.02,
            ..PARAMS
        };
        let out = choreograph(&src, &params);
        assert!(
            out.total_ticks as f32 <= params.max_stretch * src.total_ticks as f32,
            "{} ticks exceeds the {}x cap",
            out.total_ticks,
            params.max_stretch
        );
        assert!(
            out.events.iter().all(|e| e.at_tick <= out.total_ticks),
            "nothing scheduled past the end"
        );

        let loose = choreograph(
            &src,
            &Choreography {
                max_stretch: 100.0,
                ..params
            },
        );
        assert!(
            out.total_ticks < loose.total_ticks,
            "the cap squeezes the overrun ({}) below the uncapped run ({})",
            out.total_ticks,
            loose.total_ticks
        );
    }

    #[test]
    fn water_and_lift_strokes_keep_their_amounts() {
        let mut src = Timeline::new(200);
        src.push(
            0,
            brush(line(Point::new(0.1, 0.5), Point::new(0.9, 0.5)), 0.5, 0.8),
        );
        src.push(
            0,
            Operation::Water(WaterStroke {
                path: line(Point::new(0.3, 0.5), Point::new(0.7, 0.5)),
                radius: RadiusProfile::uniform(0.08),
                water: 0.7,
                softness: 0.6,
                span: StrokeSpan::FULL,
            }),
        );
        src.push(
            0,
            Operation::Lift(LiftStroke {
                path: line(Point::new(0.4, 0.5), Point::new(0.6, 0.5)),
                radius: RadiusProfile::uniform(0.06),
                strength: 0.6,
                softness: 0.7,
                span: StrokeSpan::FULL,
            }),
        );
        let out = choreograph(&src, &PARAMS);

        let water_ops: Vec<&WaterStroke> = out
            .events
            .iter()
            .filter_map(|e| match &e.op {
                Operation::Water(w)
                    if w.path == line(Point::new(0.3, 0.5), Point::new(0.7, 0.5)) =>
                {
                    Some(w)
                }
                _ => None,
            })
            .collect();
        let lift_ops: Vec<&LiftStroke> = out
            .events
            .iter()
            .filter_map(|e| match &e.op {
                Operation::Lift(l) => Some(l),
                _ => None,
            })
            .collect();
        assert!(
            water_ops.len() > 1,
            "the water stroke is drawn, not stamped"
        );
        assert!(lift_ops.len() > 1, "the lift stroke is drawn, not stamped");
        for w in &water_ops {
            assert_eq!(w.water, 0.7, "water amount unchanged");
            assert_eq!(w.radius, RadiusProfile::uniform(0.08));
        }
        for l in &lift_ops {
            assert_eq!(l.strength, 0.6, "lift strength unchanged");
            assert_eq!(l.radius, RadiusProfile::uniform(0.06));
        }
    }

    #[test]
    fn a_timeline_with_no_strokes_is_unchanged() {
        let mut src = Timeline::new(100);
        src.push(
            0,
            Operation::SetMask(Mask::Polygon {
                points: vec![
                    Point::new(0.0, 0.0),
                    Point::new(1.0, 0.0),
                    Point::new(1.0, 1.0),
                ],
                feather: 0.02,
            }),
        );
        src.push(50, Operation::DryAll);
        src.push(100, Operation::DryAll);
        let out = choreograph(&src, &PARAMS);
        assert_eq!(out, src);
    }

    #[test]
    fn retiming_keeps_stroke_order_and_never_inverts_two_ticks() {
        let mut src = Timeline::new(300);
        src.push(
            0,
            brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
        );
        src.push(
            10,
            brush(line(Point::new(0.3, 0.5), Point::new(0.9, 0.5)), 0.5, 0.8),
        );
        src.push(
            30,
            brush(line(Point::new(0.1, 0.5), Point::new(0.4, 0.5)), 0.5, 0.8),
        );
        src.push(
            30,
            brush(line(Point::new(0.5, 0.5), Point::new(0.9, 0.5)), 0.5, 0.8),
        );
        src.push(
            60,
            brush(line(Point::new(0.1, 0.5), Point::new(0.6, 0.5)), 0.5, 0.8),
        );
        src.push(200, Operation::Dry { rate: 3.0 });
        let out = choreograph(&src, &PARAMS);

        let starts = stroke_starts(&out);
        assert_eq!(starts.len(), 5);
        assert!(
            starts.windows(2).all(|w| w[0] < w[1]),
            "no two strokes invert their order"
        );
    }

    #[test]
    fn events_after_the_paint_phase_do_not_move() {
        let tail_a = line(Point::new(0.1, 0.5), Point::new(0.7, 0.5));
        let tail_b = line(Point::new(0.4, 0.5), Point::new(0.6, 0.5));
        let mut src = Timeline::new(300);
        src.push(
            0,
            brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
        );
        src.push(
            40,
            brush(line(Point::new(0.3, 0.5), Point::new(0.9, 0.5)), 0.5, 0.8),
        );
        src.push(100, Operation::Dry { rate: 3.0 });
        src.push(150, brush(tail_a.clone(), 0.5, 0.8));
        src.push(200, brush(tail_b.clone(), 0.5, 0.8));
        let out = choreograph(&src, &PARAMS);

        assert_eq!(out.total_ticks, 300, "the tail fits without growing");
        let at = |path: Vec<Point>| -> Vec<u32> {
            out.events
                .iter()
                .filter(|e| matches!(&e.op, Operation::Brush(s) if s.path == path))
                .map(|e| e.at_tick)
                .collect()
        };
        assert_eq!(at(tail_a), vec![150], "a tail stroke keeps its tick");
        assert_eq!(at(tail_b), vec![200], "a tail stroke keeps its tick");
        let dries: Vec<u32> = out
            .events
            .iter()
            .filter_map(|e| match e.op {
                Operation::Dry { .. } => Some(e.at_tick),
                _ => None,
            })
            .collect();
        assert_eq!(dries.len(), 1);
        assert!(
            dries[0] <= 100,
            "the settle Dry moves to where the pen stopped, not past its phase"
        );
    }

    /// A glaze returns evaporation to the base rate with a `Dry` of its own,
    /// so the first `Dry` in a timeline is usually the start of the second
    /// wash. Reading it as the settle collapses the pen's budget to the first
    /// glaze and strands every later stroke at its authored tick.
    #[test]
    fn a_glaze_boundary_is_not_mistaken_for_the_settle() {
        let late = line(Point::new(0.2, 0.6), Point::new(0.8, 0.6));
        let mut src = Timeline::new(400);
        src.push(
            0,
            brush(line(Point::new(0.2, 0.4), Point::new(0.8, 0.4)), 0.5, 0.8),
        );
        src.push(160, Operation::DryAll);
        src.push(160, Operation::Dry { rate: 1.0 });
        src.push(170, brush(late.clone(), 0.5, 0.8));
        src.push(360, Operation::Dry { rate: 3.0 });
        let out = choreograph(&src, &PARAMS);

        let late_ticks: Vec<u32> = out
            .events
            .iter()
            .filter(|e| matches!(&e.op, Operation::Brush(s) if s.path == late))
            .map(|e| e.at_tick)
            .collect();
        assert!(
            late_ticks.len() > 1,
            "the glazed stroke is drawn, not stamped: {late_ticks:?}"
        );
        assert!(
            *late_ticks.last().unwrap() > 160,
            "the budget spans past the glaze, to the settle: {late_ticks:?}"
        );
    }

    #[test]
    fn strokes_overlap_by_the_configured_share() {
        let mut src = Timeline::new(150);
        src.push(
            0,
            brush(line(Point::new(0.2, 0.5), Point::new(0.8, 0.5)), 0.5, 0.8),
        );
        src.push(
            0,
            brush(line(Point::new(0.3, 0.5), Point::new(0.9, 0.5)), 0.5, 0.8),
        );
        src.push(100, Operation::Dry { rate: 3.0 });
        let budget = (PARAMS.paint_spread * 100.0).round() as u32;
        let window = laydown_window(&PARAMS, budget, 0.7, 2.0 * 0.7);

        let out = choreograph(&src, &PARAMS);
        let starts = stroke_starts(&out);
        assert_eq!(
            starts[1] - starts[0],
            stroke_advance(&PARAMS, window),
            "the default overlap paces the pair"
        );

        let overlap = Choreography {
            stroke_overlap: 0.4,
            ..PARAMS
        };
        let out = choreograph(&src, &overlap);
        let starts = stroke_starts(&out);
        assert_eq!(
            starts[1] - starts[0],
            stroke_advance(&overlap, window),
            "a wider overlap draws the second stroke sooner"
        );
    }
}
