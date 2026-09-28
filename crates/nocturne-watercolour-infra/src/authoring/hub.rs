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
use super::{Painting, Stages, Style, brush, granulating_role, lift, role, tapered, water};

pub(super) const STOPS: u32 = 6;

pub(super) const IDS: [&str; 3] = ["hub-dawn-ridges", "hub-lighthouse", "hub-lakeside-cabin"];

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

/// `outer` with `hole` cut out. The mask's inside test is even-odd, so a
/// ring joined to the outline by a doubled edge is a hole; the join runs
/// between the two rings' nearest points so it crosses only painted cells,
/// where the doubled edge cancels without a seam.
fn with_hole(outer: Vec<Point>, hole: Vec<Point>) -> Vec<Point> {
    let d = |a: Point, b: Point| (a.x - b.x).powi(2) + (a.y - b.y).powi(2);
    let (i, j) = (0..outer.len())
        .flat_map(|i| (0..hole.len()).map(move |j| (i, j)))
        .min_by(|&(a, b), &(c, e)| d(outer[a], hole[b]).total_cmp(&d(outer[c], hole[e])))
        .unwrap_or((0, 0));
    let mut pts: Vec<Point> = outer[i..].iter().chain(&outer[..=i]).copied().collect();
    pts.extend(hole[j..].iter().chain(&hole[..=j]).copied());
    pts
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

/// Moonlight or lamplight on water: broken horizontal strokes, each laid
/// into a lifted strip so the light reads on the dark water, narrowing and
/// breaking up with distance from the light.
#[allow(clippy::too_many_arguments)]
fn broken_reflection(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    stream: &mut SeedStream,
    glow: usize,
    x: f32,
    (top, bottom): (f32, f32),
    (hw_top, hw_bottom): (f32, f32),
) {
    let bars = if style.fine() { 7 } else { 4 };
    let pitch = (bottom - top) / bars as f32;
    for i in 0..bars {
        let t = i as f32 / (bars - 1).max(1) as f32;
        let y = top + pitch * (i as f32 + 0.5) * (0.85 + 0.3 * unit(stream));
        let hw = (hw_top + (hw_bottom - hw_top) * t) * (0.6 + 0.8 * unit(stream));
        let cx = x + (unit(stream) - 0.5) * hw * 0.8;
        let r = (pitch * 0.32).min(0.014) * (1.0 - 0.4 * t);
        // One stroke, not dabs: a lift cut into spans lifts unevenly, which on
        // a ripple of light is the broken edge it should have.
        p.at(
            0.0,
            lift(frame.line(cx - hw, y, cx + hw, y), r * 1.4, 0.7, 0.8),
        );
        let (c, w) = style.glow(0.9 * (1.0 - 0.35 * t), 0.35);
        p.at(
            0.0,
            tapered(
                vec![frame.pt(cx - hw, y + r * 0.2), frame.pt(cx + hw * 0.9, y)],
                (r, r * 0.4),
                glow,
                c,
                w,
                0.6,
            ),
        );
    }
}

/// A painted moon: a soft bloom glazed into the sky around it, then the disc
/// itself, shaded on one side and lifted on the other so it reads round.
fn paint_moon(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    (gold, shade): (usize, usize),
    (x, y, r): (f32, f32, f32),
    sky: Vec<Point>,
) {
    p.mask(0.0, sky, 0.045);
    let (c, w) = style.glow(if style.dark() { 0.12 } else { 0.08 }, 0.5);
    p.at(0.0, brush(vec![frame.pt(x, y)], r * 2.6, gold, c, w, 1.0));
    p.mask(0.0, frame.circle(x, y, r, 48), 0.006);
    glow(p, frame, style, gold, (x, y), r * 1.3, 1.25);
    if !style.dark() {
        p.at(
            0.0,
            brush(
                vec![frame.pt(x + r * 0.45, y + r * 0.35)],
                r * 0.7,
                shade,
                style.conc(0.12),
                style.water(0.3),
                0.95,
            ),
        );
    }
    dab_lift(p, frame, (x - r * 0.35, y - r * 0.3), r * 0.45, 0.35);
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

/// Fills `poly` with a hatched wash of each `(pigment, conc)`; the hatch
/// overshoots `x0..x1` so its turns fall outside the stencil.
fn stencil_fill(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    (poly, feather): (Vec<Point>, f32),
    (x0, x1, y0, y1): (f32, f32, f32, f32),
    rows: usize,
    layers: &[(usize, f32)],
) {
    p.mask(0.0, poly, feather);
    for (i, &(pigment, conc)) in layers.iter().enumerate() {
        p.at(
            0.0,
            brush(
                frame.hatch(x0 - 0.06, x1 + 0.06, y0, y1, rows),
                frame.hatch_radius(y0, y1, rows) * 1.25,
                pigment,
                style.conc(conc),
                style.water(if i == 0 { 0.4 } else { 0.22 }),
                0.8,
            ),
        );
    }
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

/// A lighthouse on a headland at night: a sky graded down to the horizon,
/// the sea picking up its colour, the headland and the tower, then the lamp
/// lit into a beam that fades into the sky, and last its light on the water.
pub(super) fn lighthouse(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_3_1);
    let cerulean = role(palette, PigmentRole::BaseWash);
    let phthalo = role(palette, PigmentRole::Shadow);
    let rock = granulating_role(palette, &[PigmentRole::Accent, PigmentRole::Shadow]);
    let gold = role(palette, PigmentRole::Glow);
    let seed = style.seed().0;
    let dark = style.dark();
    let mut stream = style.stream(0x49);
    let horizon = 0.63;
    let sheet = Sheet {
        x0: 0.2,
        x1: 2.86,
        sky_x: (0.12, 2.75),
        rim: horizon,
        deep: 0.91,
        seed,
    };
    let (lamp_x, lamp_y) = (2.3, 0.33);
    let beam_end = (1.05, 0.3);
    let beam_at = |t: f32| {
        (
            lamp_x - 0.03 + (beam_end.0 - lamp_x + 0.03) * t,
            lamp_y + (beam_end.1 - lamp_y) * t,
        )
    };
    let headland: Vec<Point> = frame.map(&[
        Point::new(1.82, horizon + 0.012),
        Point::new(1.87, 0.605),
        Point::new(1.92, 0.585),
        Point::new(1.95, 0.54),
        Point::new(1.97, 0.505),
        Point::new(2.03, 0.487),
        Point::new(2.14, 0.475),
        Point::new(2.3, 0.468),
        Point::new(2.47, 0.472),
        Point::new(2.6, 0.487),
        Point::new(2.7, 0.51),
        Point::new(2.77, 0.55),
        Point::new(2.83, 0.6),
        Point::new(2.88, horizon + 0.012),
    ]);
    let tower_base = 0.475;
    let (lantern_top, lantern_bottom) = (lamp_y - 0.022, lamp_y + 0.022);
    let tower: Vec<Point> = frame.map(&[
        Point::new(lamp_x - 0.026, tower_base),
        Point::new(lamp_x - 0.016, lantern_bottom + 0.012),
        Point::new(lamp_x - 0.026, lantern_bottom + 0.012),
        Point::new(lamp_x - 0.026, lantern_bottom),
        Point::new(lamp_x + 0.026, lantern_bottom),
        Point::new(lamp_x + 0.026, lantern_bottom + 0.012),
        Point::new(lamp_x + 0.016, lantern_bottom + 0.012),
        Point::new(lamp_x + 0.026, tower_base),
    ]);
    let roof: Vec<Point> = frame.map(&[
        Point::new(lamp_x - 0.024, lantern_top),
        Point::new(lamp_x, lantern_top - 0.03),
        Point::new(lamp_x + 0.024, lantern_top),
    ]);
    let sky_height = 0.44;
    let sky = sheet.sky(&frame, sky_height + Sheet::SLACK, |_| horizon);
    let sea = sheet.below(&frame, |_| horizon, Sheet::SLACK);
    let rows = |fine: usize, coarse: usize| if style.fine() { fine } else { coarse };

    let mut stages = Stages::new(style.ticks(STAGE_TICKS));
    stages.stage(|p| {
        p.mask(0.0, sky.clone(), 0.03);
        let layers = if dark {
            [(phthalo, 0.1, 0.18), (cerulean, 0.0, 0.14)]
        } else {
            [(cerulean, 0.36, 0.1), (phthalo, 0.14, 0.0)]
        };
        graded(
            p,
            &frame,
            style,
            sheet.sky_x,
            (&|x| sheet.sky_top(x, sky_height, &|_| horizon), &|_| {
                horizon + 0.01
            }),
            rows(6, 3),
            &layers,
            (if dark { 0.0 } else { 0.45 }, 0.0),
        );
        if style.fine() {
            let cloud = if dark { cerulean } else { phthalo };
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                cloud,
                (0.4, 2.6, 0.26, 0.44),
                4,
                (0.07, if dark { 0.2 } else { 0.35 }),
            );
            if !dark {
                for _ in 0..3 {
                    let (x, y) = (
                        0.4 + 2.2 * unit(&mut stream),
                        0.4 + 0.12 * unit(&mut stream),
                    );
                    dab_lift(p, &frame, (x, y), 0.06, 0.35);
                }
            }
        }
        wet_edge(
            p,
            &frame,
            style,
            &sheet.sky_edge(sky_height, |_| horizon, 0.03),
            0.04,
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, sea.clone(), 0.008);
        graded(
            p,
            &frame,
            style,
            (sheet.x0, sheet.x1),
            (&|_| horizon + 0.012, &|x| sheet.floor(x) - 0.02),
            rows(5, 3),
            &[(cerulean, 0.34, 0.16), (phthalo, 0.1, 0.34)],
            (0.0, 0.5),
        );
        if style.fine() {
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                phthalo,
                (0.3, 2.6, 0.7, 0.88),
                3,
                (0.07, 0.4),
            );
            for _ in 0..2 {
                let (x, y) = (
                    0.3 + 2.0 * unit(&mut stream),
                    0.68 + 0.18 * unit(&mut stream),
                );
                p.at(
                    0.0,
                    lift(frame.line(x, y, x + 0.25, y + 0.004), 0.01, 0.5, 0.8),
                );
            }
            streak(
                p,
                &frame,
                style,
                phthalo,
                (0.35, 1.55, horizon + 0.02),
                0.018,
                0.35,
            );
        }
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        stencil_fill(
            p,
            &frame,
            style,
            (headland.clone(), 0.005),
            (1.8, 2.9, 0.46, horizon + 0.012),
            rows(7, 4),
            &[(rock, 0.6), (phthalo, 0.18)],
        );
        drop_ins(
            p,
            &frame,
            style,
            &mut stream,
            phthalo,
            (1.9, 2.8, 0.55, 0.62),
            3,
            (0.04, 0.7),
        );
        dab_lift(p, &frame, (2.1, 0.49), 0.03, 0.35);
        p.at(
            0.0,
            tapered(
                frame.map(&[
                    Point::new(1.99, 0.49),
                    Point::new(1.95, 0.54),
                    Point::new(1.9, 0.6),
                ]),
                (0.012, 0.03),
                phthalo,
                style.conc(0.7),
                style.water(0.25),
                0.8,
            ),
        );
        p.mask(0.0, sea.clone(), 0.008);
        p.at(
            0.0,
            brush(
                frame.line(1.86, horizon + 0.025, 2.86, horizon + 0.028),
                0.025,
                rock,
                style.conc(0.3),
                style.water(0.3),
                1.0,
            ),
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, tower.clone(), 0.003);
        p.at(
            0.0,
            tapered(
                frame.line(lamp_x, tower_base + 0.01, lamp_x, lantern_bottom),
                (0.032, 0.022),
                phthalo,
                style.conc(0.85),
                style.water(0.35),
                0.5,
            ),
        );
        p.at(
            0.0,
            tapered(
                frame.line(
                    lamp_x + 0.014,
                    tower_base + 0.005,
                    lamp_x + 0.01,
                    lantern_bottom + 0.012,
                ),
                (0.01, 0.007),
                phthalo,
                style.conc(1.3),
                style.water(0.2),
                0.7,
            ),
        );
        p.mask(0.0, roof.clone(), 0.003);
        p.at(
            0.0,
            brush(
                frame.line(
                    lamp_x - 0.03,
                    lantern_top - 0.012,
                    lamp_x + 0.03,
                    lantern_top - 0.012,
                ),
                0.02,
                phthalo,
                style.conc(1.0),
                style.water(0.35),
                0.5,
            ),
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(
            0.0,
            frame.rect(lamp_x - 0.026, lantern_top, lamp_x + 0.026, lantern_bottom),
            0.003,
        );
        glow(p, &frame, style, gold, (lamp_x, lamp_y), 0.04, 1.4);
        // The beam stays inside the sky, and fades out along it.
        p.mask(0.0, sky.clone(), 0.04);
        if !dark {
            let (x, y) = beam_at(1.0);
            p.at(
                0.0,
                Operation::Lift(LiftStroke {
                    path: vec![frame.pt(lamp_x - 0.04, lamp_y), frame.pt(x, y)],
                    radius: RadiusProfile {
                        start: 0.02,
                        end: 0.07,
                    },
                    strength: 0.7,
                    softness: 0.85,
                    span: StrokeSpan::FULL,
                }),
            );
        }
        let layer = if dark { 0.06 } else { 0.09 };
        for t in [1.0, 0.7, 0.45, 0.25] {
            let (c, w) = style.glow(layer, 0.5);
            let (x, y) = beam_at(t);
            p.at(
                0.0,
                tapered(
                    vec![frame.pt(lamp_x - 0.04, lamp_y), frame.pt(x, y)],
                    (0.015, 0.015 + 0.055 * t),
                    gold,
                    c,
                    w,
                    0.95,
                ),
            );
        }
        let (c, w) = style.glow(0.16, 0.5);
        p.at(
            0.0,
            brush(vec![frame.pt(lamp_x, lamp_y)], 0.09, gold, c, w, 1.0),
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, sea.clone(), 0.008);
        broken_reflection(
            p,
            &frame,
            style,
            &mut stream,
            gold,
            lamp_x,
            (horizon + 0.012, 0.88),
            (0.04, 0.012),
        );
        for &(x0, x1, y) in &[
            (0.45, 0.85, 0.72),
            (1.1, 1.6, 0.79),
            (0.7, 1.0, 0.86),
            (2.45, 2.72, 0.76),
        ] {
            streak(p, &frame, style, phthalo, (x0, x1, y), 0.007, 0.8);
        }
        p.settle(0.9, 2.5);
    });
    style.scene(
        "hub-lighthouse",
        palette,
        WIDE_3_1,
        Paper::cold_press(style.seed()),
        stages.finish(),
    )
}

fn smooth(a: f32, b: f32, x: f32) -> f32 {
    let t = ((x - a) / (b - a)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// A pine silhouette: tiers of drooping boughs from `top` to `foot`, walked
/// left to right (up the left side, over the apex, down the right) so it
/// splices into a skyline.
fn pine(x: f32, top: f32, foot: f32, w: f32) -> Vec<Point> {
    let tiers = 4;
    let (mut right, mut left) = (Vec::new(), Vec::new());
    for i in 1..=tiers {
        let t = i as f32 / tiers as f32;
        let y = top + (foot - top) * t;
        let half = w * 0.5 * t.powf(0.85);
        let notch = y - (foot - top) / tiers as f32 * 0.4;
        right.push(Point::new(x + half, y));
        left.push(Point::new(x - half, y));
        if i < tiers {
            right.push(Point::new(
                x + half * 0.45,
                notch + (foot - top) / tiers as f32 * 0.35,
            ));
            left.push(Point::new(
                x - half * 0.45,
                notch + (foot - top) / tiers as f32 * 0.35,
            ));
        }
    }
    let mut pts: Vec<Point> = left.into_iter().rev().collect();
    pts.push(Point::new(x, top));
    pts.extend(right);
    pts
}

/// A cabin on a lake shore under the moon: afterglow graded down to the
/// horizon around the moon's place, far hills, the lake picking up the sky,
/// the shore with the cabin and pines, then the moon and the lit window, and
/// last their light broken across the water.
pub(super) fn lakeside_cabin(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_3_1);
    let indigo = role(palette, PigmentRole::BaseWash);
    let grey = role(palette, PigmentRole::Shadow);
    let silhouette = grey;
    // Under luminous compositing a silhouette glows rather than darkens, so it
    // needs more paint to read against the sky.
    let dense = if style.dark() { 1.6 } else { 1.0 };
    let rose = role(palette, PigmentRole::Accent);
    let gold = role(palette, PigmentRole::Glow);
    let seed = style.seed().0;
    let dark = style.dark();
    let mut stream = style.stream(0x4a);
    let horizon = 0.57;
    let sheet = Sheet {
        x0: 0.1,
        x1: 2.76,
        sky_x: (0.16, 2.84),
        rim: horizon,
        deep: 0.91,
        seed: seed ^ 0x33,
    };
    let moon = (0.78, 0.25, 0.07);
    let hills = Range {
        floor: horizon + 0.01,
        peaks: &[(0.4, 0.32, 0.06), (1.3, 0.45, 0.11), (2.4, 0.3, 0.04)],
        wobble: 0.02,
        seed: seed ^ 0x21,
    };
    let sill = 0.615;
    let (cabin_x, cabin_w, eave, ridge_y) = (2.13, 0.2, 0.52, 0.445);
    let window = (cabin_x + 0.055, cabin_x + 0.105, 0.545, 0.585);
    let window_x = (window.0 + window.1) * 0.5;
    let lake = sheet.below(&frame, |_| horizon, Sheet::SLACK);
    let pines = [(1.94, 0.37, 0.12), (2.48, 0.31, 0.15), (2.63, 0.4, 0.11)];
    let mut outline = vec![Point::new(1.66, sill + 0.02), Point::new(1.8, sill - 0.008)];
    outline.extend(pine(pines[0].0, pines[0].1, sill, pines[0].2));
    outline.extend([
        Point::new(cabin_x, sill),
        Point::new(cabin_x, eave),
        Point::new(cabin_x - 0.022, eave + 0.004),
        Point::new(cabin_x + cabin_w * 0.5, ridge_y),
        Point::new(cabin_x + cabin_w + 0.022, eave + 0.004),
        Point::new(cabin_x + cabin_w, eave),
        Point::new(cabin_x + cabin_w, sill),
    ]);
    outline.extend(pine(pines[1].0, pines[1].1, sill, pines[1].2));
    outline.extend(pine(pines[2].0, pines[2].1, sill, pines[2].2));
    outline.extend([Point::new(2.78, sill - 0.01), Point::new(2.9, sill + 0.022)]);
    let shore = with_hole(
        frame.map(&outline),
        frame.rect(window.0, window.2, window.1, window.3),
    );
    let rows = |fine: usize, coarse: usize| if style.fine() { fine } else { coarse };

    let mut stages = Stages::new(style.ticks(STAGE_TICKS));
    stages.stage(|p| {
        p.mask(
            0.0,
            with_hole(
                sheet.sky(&frame, 0.42 + Sheet::SLACK, |_| horizon),
                frame.circle(moon.0, moon.1, moon.2 + 0.004, 48),
            ),
            0.03,
        );
        let layers = if dark {
            [(indigo, 0.1, 0.16), (rose, 0.04, 0.32)]
        } else {
            [(indigo, 0.34, 0.1), (rose, 0.02, 0.32)]
        };
        graded(
            p,
            &frame,
            style,
            sheet.sky_x,
            (&|x| sheet.sky_top(x, 0.42, &|_| horizon), &|_| {
                horizon + 0.01
            }),
            rows(6, 3),
            &layers,
            (if dark { 0.0 } else { 0.45 }, 0.0),
        );
        if style.fine() {
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                rose,
                (0.3, 2.6, 0.38, 0.52),
                3,
                (0.07, 0.4),
            );
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                grey,
                (1.2, 2.7, 0.14, 0.3),
                3,
                (0.06, if dark { 0.1 } else { 0.3 }),
            );
        }
        wet_edge(
            p,
            &frame,
            style,
            &sheet.sky_edge(0.42, |_| horizon, 0.03),
            0.04,
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, sheet.below(&frame, |x| hills.y(x), 0.0), 0.006);
        graded(
            p,
            &frame,
            style,
            (sheet.x0 + 0.1, sheet.x1),
            (&|x| hills.y(x) + 0.01, &|_| horizon + 0.02),
            rows(4, 3),
            &[(indigo, 0.32, 0.32), (grey, 0.1, 0.1)],
            (0.0, 0.0),
        );
        if style.fine() {
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                rose,
                (0.3, 2.4, horizon - 0.06, horizon - 0.02),
                3,
                (0.04, 0.3),
            );
        }
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, lake.clone(), 0.008);
        graded(
            p,
            &frame,
            style,
            (sheet.x0, sheet.x1),
            (&|_| horizon + 0.012, &|x| sheet.floor(x) - 0.02),
            rows(5, 3),
            &[(rose, 0.26, 0.0), (indigo, 0.18, 0.44)],
            (0.0, 0.5),
        );
        if style.fine() {
            drop_ins(
                p,
                &frame,
                style,
                &mut stream,
                grey,
                (0.3, 2.6, 0.7, 0.9),
                3,
                (0.07, 0.3),
            );
            for _ in 0..2 {
                let (x, y) = (
                    0.25 + 2.2 * unit(&mut stream),
                    0.66 + 0.2 * unit(&mut stream),
                );
                p.at(
                    0.0,
                    lift(frame.line(x, y, x + 0.28, y + 0.004), 0.01, 0.5, 0.8),
                );
            }
            streak(
                p,
                &frame,
                style,
                grey,
                (0.25, 1.45, horizon + 0.018),
                0.016,
                0.35,
            );
        }
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, shore.clone(), 0.004);
        p.at(
            0.0,
            brush(
                frame.line(1.6, sill - 0.002, 2.95, sill - 0.002),
                0.02,
                silhouette,
                style.conc(1.0 * dense),
                style.water(0.35),
                0.6,
            ),
        );
        for &(x, top, w) in &pines {
            p.at(
                0.0,
                tapered(
                    frame.line(x, top, x, sill),
                    (0.012, w * 0.55),
                    silhouette,
                    style.conc(0.95 * dense),
                    style.water(0.35),
                    0.6,
                ),
            );
            p.at(
                0.0,
                brush(
                    vec![frame.pt(x + w * 0.12, sill - (sill - top) * 0.25)],
                    w * 0.35,
                    indigo,
                    style.conc(0.7),
                    style.water(0.25),
                    0.95,
                ),
            );
        }
        p.at(
            0.0,
            brush(
                frame.hatch(
                    cabin_x - 0.04,
                    cabin_x + cabin_w + 0.04,
                    ridge_y + 0.01,
                    sill,
                    4,
                ),
                0.028,
                silhouette,
                style.conc(0.95 * dense),
                style.water(0.35),
                0.6,
            ),
        );
        dab_lift(p, &frame, (cabin_x + 0.02, eave - 0.01), 0.025, 0.3);
        p.mask(0.0, lake.clone(), 0.008);
        p.at(
            0.0,
            brush(
                frame.line(1.7, sill + 0.03, 2.88, sill + 0.032),
                0.03,
                silhouette,
                style.conc(0.35),
                style.water(0.3),
                1.0,
            ),
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        paint_moon(
            p,
            &frame,
            style,
            (gold, rose),
            moon,
            sheet.sky(&frame, 0.42 + Sheet::SLACK, |_| horizon),
        );
        p.mask(
            0.0,
            frame.rect(window.0, window.2, window.1, window.3),
            0.003,
        );
        glow(
            p,
            &frame,
            style,
            gold,
            (window_x, (window.2 + window.3) * 0.5),
            0.035,
            1.5,
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, lake.clone(), 0.008);
        broken_reflection(
            p,
            &frame,
            style,
            &mut stream,
            gold,
            moon.0,
            (horizon + 0.01, 0.88),
            (0.07, 0.02),
        );
        broken_reflection(
            p,
            &frame,
            style,
            &mut stream,
            gold,
            window_x,
            (sill + 0.035, sill + 0.13),
            (0.028, 0.01),
        );
        p.settle(0.9, 2.5);
    });
    style.scene(
        "hub-lakeside-cabin",
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
