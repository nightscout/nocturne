//! Setup-hub headers: wide scenes painted in [`STOPS`] stages (see
//! [`Stages`]), one per hub item, so every stop is a finished painting with
//! fewer layers rather than a painting interrupted. Every layer is clipped to
//! one irregular [`Sheet`], so the picture dissolves into the page along a
//! broken, lopsided edge instead of stopping at a frame or a stamped oval.

use nocturne_watercolour_core::domain::seed::SeedStream;
use nocturne_watercolour_core::domain::{
    LiftStroke, Operation, Palette, Paper, PigmentRole, Point, RadiusProfile, Scene, SizeHint,
    StrokeSpan,
};

use super::geometry::{Frame, value_noise_1d};
use super::{Painting, Stages, Style, brush, role, tapered, water};

pub(super) const STOPS: u32 = 6;

pub(super) const IDS: [&str; 1] = ["hub-dawn-ridges"];

const WIDE_3_1: SizeHint = SizeHint {
    width: 768,
    height: 256,
};

const STAGE_TICKS: u32 = 280;

/// The extent of a hub painting on the page. Everything is laid inside it:
/// the sky rises from the horizon to a ragged, uneven top edge and the land
/// and water fall to a ragged floor, and both thin out toward ends that sit
/// at different distances from the frame, so no two sides match.
struct Sheet {
    x0: f32,
    x1: f32,
    /// The sky's own extent, offset from the land's so the two ends differ.
    sky_x: (f32, f32),
    /// Where the floor meets the ends and how far below them it falls.
    rim: f32,
    deep: f32,
    seed: u64,
}

impl Sheet {
    const SAMPLES: usize = 240;
    /// How far a stencil's loose edge sits past the paint.
    const SLACK: f32 = 0.07;
    const PAST: f32 = 0.08;

    fn noise(&self, salt: u64, x: f32, freq: f32) -> f32 {
        value_noise_1d(self.seed ^ salt, x * freq) - 0.5
    }

    /// Where a stencil is sampled: a little past the paint on each side, so
    /// the brush's own ends, not the stencil, finish the wash.
    fn xs(&self) -> impl Iterator<Item = f32> + '_ {
        let (a, b) = (
            (self.x0 - Self::PAST).max(0.0),
            (self.x1 + Self::PAST).min(3.0),
        );
        (0..=Self::SAMPLES).map(move |i| a + (b - a) * i as f32 / Self::SAMPLES as f32)
    }

    /// `0` at the ends, `1` across the body; the shoulders differ in width
    /// and wander, so the silhouette is lopsided.
    fn envelope(&self, x: f32, salt: u64, shoulders: (f32, f32)) -> f32 {
        let u = ((x - self.x0) / (self.x1 - self.x0)).clamp(0.0, 1.0);
        let rise = smooth(0.0, shoulders.0, u) * smooth(0.0, shoulders.1, 1.0 - u);
        (rise * (0.9 + 0.4 * self.noise(salt, x, 1.7))).clamp(0.0, 1.0)
    }

    /// A dry-brush edge: fine, sharp wobble that breaks the contour.
    fn ragged(&self, salt: u64, x: f32) -> f32 {
        self.noise(salt, x, 23.0) * 0.016 + self.noise(salt ^ 0x5, x, 57.0) * 0.008
    }

    fn floor(&self, x: f32) -> f32 {
        let e = 0.25 + 0.75 * self.envelope(x, 0x71, (0.2, 0.33));
        self.rim
            + (self.deep - self.rim) * e.powf(0.6)
            + e * (self.noise(0x72, x, 3.1) * 0.09 + self.noise(0x73, x, 9.0) * 0.03)
            + self.ragged(0x74, x) * e.sqrt()
    }

    /// The part of the sheet below `upper(x)`, its floor `slack` further
    /// down than the paint goes, so the wash ends on its own edge.
    fn below(&self, frame: &Frame, upper: impl Fn(f32) -> f32, slack: f32) -> Vec<Point> {
        let spans: Vec<(f32, f32, f32)> = self
            .xs()
            .map(|x| (x, upper(x), self.floor(x) + slack))
            .filter(|&(_, t, b)| t < b - 0.004)
            .collect();
        let mut pts: Vec<Point> = spans.iter().map(|&(x, t, _)| frame.pt(x, t)).collect();
        pts.extend(spans.iter().rev().map(|&(x, _, b)| frame.pt(x, b)));
        pts
    }

    /// The sky's top edge above `ground(x)`: up to `height`, rising and
    /// falling along its length like a sky laid with a loaded brush.
    fn sky_top(&self, x: f32, height: f32, ground: &impl Fn(f32) -> f32) -> f32 {
        let u = ((x - self.sky_x.0) / (self.sky_x.1 - self.sky_x.0)).clamp(0.0, 1.0);
        let e = 0.3 + 0.7 * smooth(0.0, 0.3, u) * smooth(0.0, 0.22, 1.0 - u);
        let swell =
            0.62 + 0.75 * (self.noise(0x76, x, 1.3) + 0.5) * 0.8 + self.noise(0x77, x, 4.2) * 0.25;
        ground(x) - height * e.sqrt() * swell.clamp(0.3, 1.2) + self.ragged(0x78, x) * e.sqrt()
    }

    /// Sky from its top edge down to just below `ground(x)`.
    fn sky(&self, frame: &Frame, height: f32, ground: impl Fn(f32) -> f32) -> Vec<Point> {
        let (a, b) = (
            (self.sky_x.0 - Self::PAST).max(0.0),
            (self.sky_x.1 + Self::PAST).min(3.0),
        );
        let spans: Vec<(f32, f32, f32)> = (0..=Self::SAMPLES)
            .map(|i| a + (b - a) * i as f32 / Self::SAMPLES as f32)
            .map(|x| (x, self.sky_top(x, height, &ground), ground(x) + 0.004))
            .filter(|&(_, t, b)| t < b - 0.006)
            .collect();
        let mut pts: Vec<Point> = spans.iter().map(|&(x, t, _)| frame.pt(x, t)).collect();
        pts.extend(spans.iter().rev().map(|&(x, _, b)| frame.pt(x, b)));
        pts
    }

    /// A path `inset` inside the sky's top edge.
    fn sky_edge(&self, height: f32, ground: impl Fn(f32) -> f32, inset: f32) -> Vec<(f32, f32)> {
        let (a, b) = self.sky_x;
        (0..=16)
            .map(|i| a + 0.05 + (b - a - 0.1) * i as f32 / 16.0)
            .map(|x| (x, self.sky_top(x, height, &ground) + inset))
            .collect()
    }
}

/// Clean water run along `path` into a wet wash: it pushes the pigment
/// aside and blooms, so an edge softens into a pale, broken fringe instead
/// of stopping on a line.
fn wet_edge(p: &mut Painting, frame: &Frame, style: &Style, path: &[(f32, f32)], radius: f32) {
    if !style.fine() || style.dark() {
        return;
    }
    let pts: Vec<Point> = path.iter().map(|&(x, y)| frame.pt(x, y)).collect();
    p.at(0.0, water(pts, radius, style.water(0.7), 0.85));
}

/// A ridge line: `floor` is the valley height and each peak `(centre_x,
/// half_width, rise)` lifts it.
struct Range {
    floor: f32,
    peaks: &'static [(f32, f32, f32)],
    wobble: f32,
    seed: u64,
}

impl Range {
    fn y(&self, x: f32) -> f32 {
        let rise: f32 = self
            .peaks
            .iter()
            .map(|&(cx, hw, h)| h * (-((x - cx) / hw).powi(2)).exp())
            .sum();
        self.floor
            - rise
            - (value_noise_1d(self.seed, x * 7.0) - 0.5) * self.wobble
            - (value_noise_1d(self.seed ^ 1, x * 29.0) - 0.5) * self.wobble * 0.3
    }

    fn crest(&self) -> f32 {
        self.peaks
            .iter()
            .map(|&(_, _, h)| self.floor - h)
            .fold(self.floor, f32::min)
    }
}

/// A disc clipped to stay above `ridge`: a sun that rises from behind land.
fn disc_above(
    frame: &Frame,
    (cx, cy, r): (f32, f32, f32),
    ridge: impl Fn(f32) -> f32,
) -> Vec<Point> {
    (0..48)
        .map(|i| {
            let a = i as f32 / 48.0 * std::f32::consts::TAU;
            let x = cx + r * a.cos();
            frame.pt(x, (cy + r * a.sin()).min(ridge(x) - 0.003))
        })
        .collect()
}

fn smooth(a: f32, b: f32, x: f32) -> f32 {
    let t = ((x - a) / (b - a)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// Uniform `0..1` from a seeded stream.
fn unit(stream: &mut SeedStream) -> f32 {
    (stream.next_u64() >> 40) as f32 / (1u64 << 24) as f32
}

fn dab_lift(p: &mut Painting, frame: &Frame, (x, y): (f32, f32), r: f32, strength: f32) {
    p.at(
        0.0,
        Operation::Lift(LiftStroke {
            path: vec![frame.pt(x, y)],
            radius: RadiusProfile::uniform(r),
            strength,
            softness: 0.9,
            span: StrokeSpan::FULL,
        }),
    );
}

/// A graded wash laid in rows from `upper(x)` down to `lower(x)` across
/// `x0..x1`, each row following the band's shape: each
/// row lays every `(pigment, top, bottom)` layer at its graded concentration,
/// alternating direction, so rows and pigments run together wet. The rows
/// stop short of the span by different amounts, so the wash's sides are the
/// brush's own staggered ends, not a stencil, and the pen's steps along one
/// row never line up with the next row's into a vertical seam.
#[allow(clippy::too_many_arguments)]
fn graded(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    (x0, x1): (f32, f32),
    (upper, lower): (&dyn Fn(f32) -> f32, &dyn Fn(f32) -> f32),
    rows: usize,
    layers: &[(usize, f32, f32)],
    (fade_top, fade_bottom): (f32, f32),
) {
    let mid = (x0 + x1) * 0.5;
    let radius = frame.hatch_radius(upper(mid), lower(mid), rows) * 1.5;
    for i in 0..rows {
        let t = i as f32 / (rows - 1).max(1) as f32;
        // Rows within `fade_top` / `fade_bottom` of an edge run thin, so the
        // wash pales out toward that edge instead of stopping on it.
        let ramp = |d: f32, share: f32| {
            if share > 0.0 {
                0.3 + 0.7 * smooth(0.0, share, d)
            } else {
                1.0
            }
        };
        let edge = ramp(t, fade_top) * ramp(1.0 - t, fade_bottom);
        let left = x0 + 0.28 * ((i as f32 * 0.618_034 + 0.1).fract());
        let right = x1 - 0.28 * ((i as f32 * 0.414_214 + 0.3).fract());
        let (a, b) = if i % 2 == 0 {
            (left, right)
        } else {
            (right, left)
        };
        let path: Vec<Point> = (0..=16)
            .map(|k| {
                let x = a + (b - a) * k as f32 / 16.0;
                frame.pt(x, upper(x) + (lower(x) - upper(x)) * t)
            })
            .collect();
        for &(pigment, top, bottom) in layers {
            let conc = (top + (bottom - top) * t) * edge;
            // Skip only a layer that is empty throughout, so which rows are laid
            // never depends on the ground (a dark ground must not add marks).
            if top.max(bottom) > 0.0 {
                p.at(
                    0.0,
                    brush(
                        path.clone(),
                        radius,
                        pigment,
                        style.conc(conc),
                        style.water(0.4),
                        0.9,
                    ),
                );
            }
        }
    }
}

/// Wet-into-wet: loaded dabs of `pigment` dropped into the still-wet wash
/// at seeded places inside `(x0, x1, y0, y1)`, where they bloom and bleed.
#[allow(clippy::too_many_arguments)]
fn drop_ins(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    stream: &mut SeedStream,
    pigment: usize,
    (x0, x1, y0, y1): (f32, f32, f32, f32),
    count: usize,
    (radius, conc): (f32, f32),
) {
    for _ in 0..count {
        let (x, y) = (x0 + (x1 - x0) * unit(stream), y0 + (y1 - y0) * unit(stream));
        let r = radius * (0.6 + 0.8 * unit(stream));
        let dx = r * (0.5 + unit(stream));
        p.at(
            0.0,
            brush(
                vec![frame.pt(x - dx, y), frame.pt(x + dx, y + r * 0.1)],
                r,
                pigment,
                style.conc(conc * (0.7 + 0.6 * unit(stream))),
                style.water(0.3),
                0.95,
            ),
        );
    }
}

fn glow(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    pigment: usize,
    (x, y): (f32, f32),
    r: f32,
    conc: f32,
) {
    let (c, w) = style.glow(conc, 0.6);
    p.at(
        0.0,
        brush(
            vec![frame.pt(x - r * 0.3, y), frame.pt(x + r * 0.3, y)],
            r,
            pigment,
            c,
            w,
            0.25,
        ),
    );
}

/// A bird in flight: two tapered wing strokes meeting at the body.
fn bird(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    pigment: usize,
    (x, y): (f32, f32),
    span: f32,
) {
    let lift = span * 0.3;
    for side in [-1.0f32, 1.0] {
        p.at(
            0.0,
            tapered(
                vec![
                    frame.pt(x + side * span * 0.5, y - lift * 0.4),
                    frame.pt(x + side * span * 0.25, y - lift),
                    frame.pt(x, y),
                ],
                (0.004, 0.009),
                pigment,
                style.conc(1.2),
                style.water(0.2),
                0.35,
            ),
        );
    }
}

/// A long wet-on-dry streak that thins toward its right end, the way a
/// cloud bar or a ripple is laid.
fn streak(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    pigment: usize,
    (x0, x1, y): (f32, f32, f32),
    r: f32,
    conc: f32,
) {
    p.at(
        0.0,
        tapered(
            vec![
                frame.pt(x0, y + r * 0.3),
                frame.pt((x0 + x1) * 0.5, y - r * 0.2),
                frame.pt(x1, y + r * 0.2),
            ],
            (r, r * 0.3),
            pigment,
            style.conc(conc),
            style.water(0.3),
            0.85,
        ),
    );
}

/// Dawn over folded ridges: a warm sky, three ridges glazed nearer and
/// darker with mist lifted along each foot, then the sun rising in the
/// saddle, and last the morning's cloud bars and birds.
pub(super) fn dawn_ridges(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_3_1);
    let rose = role(palette, PigmentRole::BaseWash);
    let indigo = role(palette, PigmentRole::Shadow);
    let ember = role(palette, PigmentRole::Accent);
    let gold = role(palette, PigmentRole::Glow);
    let seed = style.seed().0;
    let dark = style.dark();
    let mut stream = style.stream(0x48);
    let sheet = Sheet {
        x0: 0.1,
        x1: 2.9,
        sky_x: (0.22, 2.82),
        rim: 0.6,
        deep: 0.9,
        seed,
    };
    let far = Range {
        floor: 0.6,
        peaks: &[(0.5, 0.3, 0.09), (1.3, 0.38, 0.17), (2.6, 0.33, 0.12)],
        wobble: 0.025,
        seed: seed ^ 0x11,
    };
    let mid = Range {
        floor: 0.7,
        peaks: &[(0.35, 0.4, 0.12), (1.05, 0.3, 0.05), (2.15, 0.45, 0.1)],
        wobble: 0.02,
        seed: seed ^ 0x12,
    };
    let near = Range {
        floor: 0.82,
        peaks: &[(0.8, 0.5, 0.07), (2.6, 0.4, 0.05)],
        wobble: 0.015,
        seed: seed ^ 0x13,
    };
    let sun = (1.96, 0.55, 0.11);
    let rows = |fine: usize, coarse: usize| if style.fine() { fine } else { coarse };
    let mut stages = Stages::new(style.ticks(STAGE_TICKS));
    stages.stage(|p| {
        p.mask(
            0.0,
            sheet.sky(&frame, 0.42 + Sheet::SLACK, |x| far.y(x)),
            0.03,
        );
        let k = if dark { 1.15 } else { 1.0 };
        graded(
            p,
            &frame,
            style,
            sheet.sky_x,
            (&|x| sheet.sky_top(x, 0.42, &|x| far.y(x)), &|x| {
                far.y(x) + 0.02
            }),
            rows(6, 3),
            &[
                (rose, 0.3 * k, 0.12 * k),
                (gold, 0.0, 0.55 * k),
                (indigo, if dark { 0.0 } else { 0.08 }, 0.0),
            ],
            (if dark { 0.0 } else { 0.45 }, 0.0),
        );
        if style.fine() {
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                rose,
                (0.3, 2.7, 0.2, 0.36),
                4,
                (0.06, 0.4),
            );
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                gold,
                (1.6, 2.4, 0.4, 0.52),
                2,
                (0.08, 0.5),
            );
        }
        wet_edge(
            p,
            &frame,
            style,
            &sheet.sky_edge(0.42, |x| far.y(x), 0.03),
            0.04,
        );
        p.settle(0.9, 2.5);
    });
    for (range, layers, mist) in [
        (&far, vec![(rose, 0.26), (indigo, 0.1)], rose),
        (&mid, vec![(indigo, 0.3), (rose, 0.14)], rose),
        (&near, vec![(indigo, 0.75), (rose, 0.08)], indigo),
    ] {
        stages.stage(|p| {
            p.mask(
                0.0,
                sheet.below(&frame, |x| range.y(x), Sheet::SLACK),
                0.006,
            );
            let flat: Vec<(usize, f32, f32)> = layers.iter().map(|&(g, c)| (g, c, c)).collect();
            graded(
                p,
                &frame,
                style,
                (sheet.x0, sheet.x1),
                (&|x| range.y(x) + 0.015, &|x| sheet.floor(x) - 0.02),
                rows(6, 4),
                &flat,
                (0.0, 0.5),
            );
            if style.fine() {
                drop_ins(
                    p,
                    &frame,
                    style,
                    &mut stream,
                    mist,
                    (0.3, 2.7, range.crest() + 0.02, range.floor),
                    3,
                    (0.07, 0.35),
                );
                let y = range.floor + 0.04;
                wet_edge(
                    p,
                    &frame,
                    style,
                    &[(0.3, y), (1.5, y - 0.01), (2.7, y + 0.01)],
                    0.03,
                );
            }

            p.settle(0.9, 2.5);
        });
    }
    stages.stage(|p| {
        p.mask(
            0.0,
            disc_above(&frame, (sun.0, sun.1, sun.2 * 2.6), |x| far.y(x)),
            0.1,
        );
        glow(
            p,
            &frame,
            style,
            gold,
            (sun.0, sun.1 - 0.03),
            sun.2 * 2.2,
            if dark { 0.18 } else { 0.16 },
        );
        p.mask(0.0, disc_above(&frame, sun, |x| far.y(x)), 0.005);
        glow(p, &frame, style, gold, (sun.0, sun.1), sun.2 * 1.4, 1.3);
        if !dark {
            p.at(
                0.0,
                brush(
                    vec![frame.pt(sun.0 + sun.2 * 0.3, sun.1 + sun.2 * 0.5)],
                    sun.2 * 0.7,
                    ember,
                    style.conc(0.3),
                    style.water(0.3),
                    0.95,
                ),
            );
        }
        dab_lift(
            p,
            &frame,
            (sun.0 - sun.2 * 0.3, sun.1 - sun.2 * 0.4),
            sun.2 * 0.4,
            0.3,
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, sheet.sky(&frame, 0.6, |x| far.y(x) - 0.012), 0.03);
        let bar = if dark { gold } else { ember };
        for &(pigment, x0, x1, y, conc) in &[
            (bar, 1.6, 2.4, 0.49, 0.35),
            (rose, 0.55, 1.2, 0.4, 0.32),
            (rose, 2.2, 2.72, 0.44, 0.28),
        ] {
            p.at(
                0.0,
                water(
                    frame.line(x0, y, x1, y + 0.004),
                    0.03,
                    style.water(0.6),
                    0.6,
                ),
            );
            streak(
                p,
                &frame,
                style,
                pigment,
                (x0 + 0.04, x1 - 0.06, y),
                0.018,
                conc,
            );
        }
        if style.fine() {
            for &(x, y, span) in &[(1.02, 0.25, 0.12), (1.18, 0.2, 0.095), (1.27, 0.3, 0.075)] {
                bird(p, &frame, style, indigo, (x, y), span);
            }
        }
        p.settle(0.9, 2.5);
    });
    style.scene(
        "hub-dawn-ridges",
        palette,
        WIDE_3_1,
        Paper::cold_press(style.seed()),
        stages.finish(),
    )
}

#[cfg(test)]
mod tests {
    use nocturne_watercolour_core::domain::{Background, Operation, Palette, Seed};

    use super::{IDS, STOPS};
    use crate::authoring::{ArtworkCatalogue, DetailLevel};

    fn is_stroke(op: &Operation) -> bool {
        matches!(
            op,
            Operation::Brush(_) | Operation::Water(_) | Operation::Lift(_)
        )
    }

    /// A seek to `k / STOPS` of the ticks has to land on a finished stage:
    /// choreography must not stretch a stage past its window, every stage must
    /// paint something, and each must be dry on the tick before the next.
    #[test]
    fn every_stop_lands_on_a_finished_dry_stage() {
        let palette = Palette::moonlight();
        for id in IDS {
            for detail in DetailLevel::ALL {
                for background in [Background::Transparent, Background::TransparentOnDark] {
                    let drawn =
                        ArtworkCatalogue::by_id_for(id, Seed(7), &palette, 0.7, detail, background)
                            .unwrap();
                    let authored = ArtworkCatalogue::by_id_for_unchoreographed(
                        id,
                        Seed(7),
                        &palette,
                        0.7,
                        detail,
                        background,
                        None,
                    )
                    .unwrap();
                    let total = drawn.timeline.total_ticks;
                    let label = format!("{id} / {detail:?} / {background:?}");
                    assert_eq!(total, authored.timeline.total_ticks, "{label} stretched");
                    assert_eq!(total % STOPS, 0, "{label}");
                    let window = total / STOPS;
                    for k in 0..STOPS {
                        let (from, to) = (k * window, (k + 1) * window);
                        let stage: Vec<_> = drawn
                            .timeline
                            .events
                            .iter()
                            .filter(|e| e.at_tick >= from && e.at_tick < to)
                            .collect();
                        assert!(
                            stage.iter().any(|e| is_stroke(&e.op)),
                            "{label}: stage {k} paints nothing"
                        );
                        assert!(
                            stage
                                .iter()
                                .any(|e| e.at_tick == to - 1 && e.op == Operation::DryAll),
                            "{label}: stage {k} is not dry at its stop"
                        );
                        let last_stroke = stage
                            .iter()
                            .filter(|e| is_stroke(&e.op))
                            .map(|e| e.at_tick)
                            .max()
                            .unwrap();
                        assert!(
                            last_stroke < to - window / 4,
                            "{label}: stage {k} leaves no time to settle"
                        );
                    }
                }
            }
        }
    }
}
