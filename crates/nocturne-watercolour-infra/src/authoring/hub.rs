//! Setup-hub headers: wide scenes painted in [`STOPS`] stages (see
//! [`Stages`]), one per hub item, so every stop is a finished painting with
//! fewer layers rather than a painting interrupted. The paper is the sky; the
//! land and water sit in a [`Lens`] that tapers to points at the horizon, so
//! the picture ends on the page instead of at the frame.

use nocturne_watercolour_core::domain::{
    LiftStroke, Operation, Palette, Paper, PigmentRole, Point, RadiusProfile, Scene, SizeHint,
    StrokeSpan,
};

use super::geometry::{Frame, value_noise_1d};
use super::{Painting, Stages, Style, brush, granulating_role, role, tapered, water};

pub(super) const STOPS: u32 = 6;

pub(super) const IDS: [&str; 3] = ["hub-dawn-ridges", "hub-lighthouse", "hub-lakeside-cabin"];

const WIDE_3_1: SizeHint = SizeHint {
    width: 768,
    height: 256,
};

const STAGE_TICKS: u32 = 200;

/// The outline every land and water layer of a hub scene is clipped to: its
/// floor dips from `rim` at the ends to `deep` in the middle, with a wobble.
struct Lens {
    cx: f32,
    rx: f32,
    rim: f32,
    deep: f32,
    power: f32,
    wobble: f32,
    seed: u64,
}

impl Lens {
    const SAMPLES: usize = 120;

    fn floor(&self, x: f32) -> f32 {
        let u = ((x - self.cx) / self.rx).abs().min(1.0);
        let depth = (1.0 - u.powf(self.power)).max(0.0).powf(1.0 / self.power);
        let wobble = value_noise_1d(self.seed ^ 0x72, x * 3.0) - 0.5
            + (value_noise_1d(self.seed ^ 0x73, x * 11.0) - 0.5) * 0.4;
        self.rim + (self.deep - self.rim) * depth + wobble * 2.0 * self.wobble * depth
    }

    /// The part of the lens below `upper(x)`.
    fn below(&self, frame: &Frame, upper: impl Fn(f32) -> f32) -> Vec<Point> {
        let spans: Vec<(f32, f32, f32)> = (0..=Self::SAMPLES)
            .map(|i| {
                let x = self.cx - self.rx + 2.0 * self.rx * i as f32 / Self::SAMPLES as f32;
                (x, upper(x), self.floor(x))
            })
            .filter(|&(_, t, b)| t < b - 0.004)
            .collect();
        let mut pts: Vec<Point> = spans.iter().map(|&(x, t, _)| frame.pt(x, t)).collect();
        pts.extend(spans.iter().rev().map(|&(x, _, b)| frame.pt(x, b)));
        pts
    }

    /// A band of sky up to `height` above `ground(x)`, thinning to nothing
    /// toward the lens's ends.
    fn sky(&self, frame: &Frame, height: f32, ground: impl Fn(f32) -> f32) -> Vec<Point> {
        let reach = self.rx * 0.97;
        let xs: Vec<f32> = (0..=Self::SAMPLES)
            .map(|i| self.cx - reach + 2.0 * reach * i as f32 / Self::SAMPLES as f32)
            .collect();
        let rise = |x: f32| {
            let u = ((x - self.cx) / reach).clamp(-1.0, 1.0);
            height * (1.0 - u * u).sqrt()
        };
        let mut pts: Vec<Point> = xs
            .iter()
            .map(|&x| frame.pt(x, ground(x) - rise(x)))
            .collect();
        pts.extend(xs.iter().rev().map(|&x| frame.pt(x, ground(x) + 0.004)));
        pts
    }
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
        self.floor - rise - (value_noise_1d(self.seed, x * 7.0) - 0.5) * self.wobble
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

/// A reflection gap in water: a column hanging from `top` under `x`,
/// narrowing from `hw.0` to `hw.1`, its edges broken so it reads as moving
/// light.
fn reflection(
    frame: &Frame,
    x: f32,
    (top, bottom): (f32, f32),
    hw: (f32, f32),
    seed: u64,
) -> Vec<Point> {
    let steps = 16;
    let edge = |side: f32| -> Vec<Point> {
        (0..=steps)
            .map(|i| {
                let t = i as f32 / steps as f32;
                let half = hw.0 + (hw.1 - hw.0) * t;
                let wob =
                    (value_noise_1d(seed ^ u64::from(side > 0.0), t * 5.0) - 0.5) * half * 0.25;
                frame.pt(x + side * (half + wob), top + (bottom - top) * t)
            })
            .collect()
    };
    let mut pts = edge(-1.0);
    pts.extend(edge(1.0).into_iter().rev());
    pts
}

/// A damp brush dabbed along `from..to`, lifting up to `strength` of the
/// paint under it, the dabs widening from `radius.0` to `radius.1`.
///
/// Dabs rather than one stroke: the choreography cuts a stroke into spans,
/// and lifts multiply, so a lift drawn in spans lifts less where two spans
/// overlap and dries into a dashed line. A single-point dab is never cut.
fn lift_along(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    (from, to): ((f32, f32), (f32, f32)),
    radius: (f32, f32),
    strength: f32,
) {
    // Every dab takes the pen at least two ticks, so shorter stages afford
    // fewer; a fifth of the stage leaves the rest of its strokes room.
    let max_dabs = (style.ticks(STAGE_TICKS) / 5).max(4) as usize;
    let length = ((to.0 - from.0).powi(2) + (to.1 - from.1).powi(2))
        .sqrt()
        .max(1e-4);
    let r_at = |t: f32| radius.0 + (radius.1 - radius.0) * t;
    // Each dab sits `pitch` of its own radius past the last, so a widening
    // lift stays evenly overlapped; the pitch opens up if that needs too many.
    let place = |pitch: f32| {
        let mut ts = vec![0.0f32];
        while let Some(&t) = ts.last() {
            let next = t + pitch * r_at(t) / length;
            if next >= 1.0 {
                break;
            }
            ts.push(next);
        }
        ts
    };
    let mut pitch = 0.45;
    let mut ts = place(pitch);
    if ts.len() > max_dabs {
        pitch *= ts.len() as f32 / max_dabs as f32;
        ts = place(pitch);
    }
    // About `2 / pitch` dabs overlap any point.
    let each = 1.0 - (1.0 - strength.clamp(0.0, 0.99)).powf(pitch * 0.5);
    for t in ts {
        p.at(
            0.0,
            Operation::Lift(LiftStroke {
                path: vec![frame.pt(from.0 + (to.0 - from.0) * t, from.1 + (to.1 - from.1) * t)],
                radius: RadiusProfile::uniform(radius.0 + (radius.1 - radius.0) * t),
                strength: each,
                softness: 0.8,
                span: StrokeSpan::FULL,
            }),
        );
    }
}

/// Light laid on dry water the way a painter does it: a damp brush lifts a
/// soft column out of the wash under `x`, then `glow` is glazed into the
/// `gap` stencil inside it, so the reflection is crisp at its core and fades
/// into the water at its edges.
#[allow(clippy::too_many_arguments)]
fn lit_reflection(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    glow: usize,
    gap: &[Point],
    x: f32,
    (top, bottom): (f32, f32),
    radius: (f32, f32),
) {
    lift_along(
        p,
        frame,
        style,
        ((x, top + radius.0), (x, bottom)),
        (radius.0 * 1.4, radius.1 * 1.4),
        0.85,
    );
    p.mask(0.0, gap.to_vec(), 0.006);
    let (c, w) = style.glow(0.85, 0.5);
    p.at(
        0.0,
        tapered(frame.line(x, top, x, bottom), radius, glow, c, w, 0.8),
    );
    p.clear_mask(0.0);
}

/// A graded sky laid in rows from `y0` down to `y1`, the way a sky is
/// painted wet: each row lays every `(pigment, top, bottom)` layer at its
/// graded concentration, alternating direction, so neighbouring rows and
/// pigments run together. The stencil's feather fades the ends; each row
/// overshoots by a different amount, so the pen's steps along one row never
/// line up with the next row's into a vertical seam.
fn graded_sky(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    (cx, reach): (f32, f32),
    (y0, y1): (f32, f32),
    rows: usize,
    layers: &[(usize, f32, f32)],
) {
    let radius = frame.hatch_radius(y0, y1, rows) * 1.4;
    for i in 0..rows {
        let t = i as f32 / (rows - 1).max(1) as f32;
        let y = y0 + (y1 - y0) * t;
        let left = cx - reach - 0.05 - 0.2 * ((i as f32 * 0.618_034).fract());
        let right = cx + reach + 0.05 + 0.2 * ((i as f32 * 0.414_214 + 0.3).fract());
        let (a, b) = if i % 2 == 0 {
            (left, right)
        } else {
            (right, left)
        };
        for &(pigment, top, bottom) in layers {
            let conc = top + (bottom - top) * t.powf(1.5);
            if conc > 0.01 {
                p.at(
                    0.0,
                    brush(
                        frame.line(a, y, b, y),
                        radius,
                        pigment,
                        style.conc(conc),
                        style.water(0.38),
                        0.9,
                    ),
                );
            }
        }
    }
}

/// Fills `poly` with a flat hatched wash of each `(pigment, conc)`; the
/// hatch overshoots `x0..x1` so its turns fall outside the stencil.
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

/// Dawn over folded ridges: a warm glow along the far skyline, three ridges
/// glazed nearer and darker, then the sun rising in the saddle and the
/// morning's cloud bars and birds.
pub(super) fn dawn_ridges(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_3_1);
    let rose = role(palette, PigmentRole::BaseWash);
    let indigo = role(palette, PigmentRole::Shadow);
    let ember = role(palette, PigmentRole::Accent);
    let gold = role(palette, PigmentRole::Glow);
    let seed = style.seed().0;
    let dark = style.dark();
    let lens = Lens {
        cx: 1.5,
        rx: 1.42,
        rim: 0.58,
        deep: 0.98,
        power: 2.2,
        wobble: 0.02,
        seed,
    };
    let far = Range {
        floor: 0.6,
        peaks: &[(0.5, 0.3, 0.09), (1.3, 0.38, 0.17), (2.6, 0.33, 0.12)],
        wobble: 0.025,
        seed: seed ^ 0x11,
    };
    let mid = Range {
        floor: 0.73,
        peaks: &[(0.35, 0.4, 0.12), (1.05, 0.3, 0.05), (2.15, 0.45, 0.1)],
        wobble: 0.02,
        seed: seed ^ 0x12,
    };
    let near = Range {
        floor: 0.88,
        peaks: &[(0.8, 0.5, 0.1), (2.6, 0.4, 0.07)],
        wobble: 0.015,
        seed: seed ^ 0x13,
    };
    let sun = (1.96, 0.55, 0.115);
    let rows = |fine: usize, coarse: usize| if style.fine() { fine } else { coarse };
    let mut stages = Stages::new(style.ticks(STAGE_TICKS));
    stages.stage(|p| {
        let (height, top, warm) = if dark {
            (0.24, 0.4, 1.2)
        } else {
            (0.4, 0.24, 1.0)
        };
        p.mask(0.0, lens.sky(&frame, height, |x| far.y(x)), 0.06);
        graded_sky(
            p,
            &frame,
            style,
            (lens.cx, lens.rx),
            (top, 0.62),
            rows(5, 3),
            &[(rose, 0.0, 0.16 * warm), (gold, 0.0, 0.5 * warm)],
        );
        p.settle(0.9, 2.5);
    });
    for (range, layers) in [
        (&far, vec![(rose, 0.26), (indigo, 0.1)]),
        (&mid, vec![(indigo, 0.3), (rose, 0.14)]),
        (&near, vec![(indigo, 0.8)]),
    ] {
        stages.stage(|p| {
            stencil_fill(
                p,
                &frame,
                style,
                (lens.below(&frame, |x| range.y(x)), 0.007),
                (0.0, 3.0, range.crest() - 0.02, lens.deep),
                rows(8, 5),
                &layers,
            );
            p.settle(0.9, 2.5);
        });
    }
    stages.stage(|p| {
        if !dark {
            p.mask(0.0, lens.sky(&frame, 0.3, |x| far.y(x) - 0.006), 0.06);
            glow(
                p,
                &frame,
                style,
                gold,
                (sun.0, sun.1 - 0.03),
                sun.2 * 2.4,
                0.2,
            );
        }
        p.mask(0.0, disc_above(&frame, sun, |x| far.y(x)), 0.004);
        glow(p, &frame, style, gold, (sun.0, sun.1), sun.2 * 1.4, 1.3);
        if !dark {
            p.at(
                0.0,
                brush(
                    frame.line(
                        sun.0 - sun.2,
                        sun.1 + sun.2 * 0.5,
                        sun.0 + sun.2,
                        sun.1 + sun.2 * 0.5,
                    ),
                    sun.2 * 0.5,
                    ember,
                    style.conc(0.3),
                    style.water(0.3),
                    0.9,
                ),
            );
        }
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, lens.sky(&frame, 0.6, |x| far.y(x) - 0.012), 0.03);
        let bar = if dark { gold } else { ember };
        for &(pigment, x0, x1, y, conc) in &[
            (bar, 1.6, 2.4, 0.5, 0.35),
            (rose, 0.55, 1.2, 0.38, 0.32),
            (rose, 2.2, 2.72, 0.43, 0.28),
        ] {
            let y = if dark { f32::max(y, 0.45) + 0.02 } else { y };
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
            for &(x, y, span) in &[(1.02, 0.22, 0.12), (1.18, 0.17, 0.095), (1.27, 0.27, 0.075)] {
                bird(p, &frame, style, indigo, (x, y), span);
            }
        }
        p.settle(0.9, 2.5);
    });
    style.scene(
        "hub-dawn-ridges",
        palette,
        WIDE_3_1,
        Paper::hot_press(style.seed()),
        stages.finish(),
    )
}

/// A lighthouse on a headland at night: twilight along the horizon with the
/// beam's path held back, the sea around the lamp's reflection, the headland
/// and the tower, then the lamp lit into the beam, and last its light on the
/// water.
pub(super) fn lighthouse(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_3_1);
    let cerulean = role(palette, PigmentRole::BaseWash);
    let phthalo = role(palette, PigmentRole::Shadow);
    let rock = granulating_role(palette, &[PigmentRole::Accent, PigmentRole::Shadow]);
    let gold = role(palette, PigmentRole::Glow);
    let seed = style.seed().0;
    let horizon = 0.63;
    let lens = Lens {
        cx: 1.5,
        rx: 1.42,
        rim: horizon,
        deep: 0.97,
        power: 2.2,
        wobble: 0.02,
        seed,
    };
    let (lamp_x, lamp_y) = (2.3, 0.33);
    let beam_end = (0.95, 0.27);
    let beam_at = |t: f32| {
        (
            lamp_x - 0.03 + (beam_end.0 - lamp_x + 0.03) * t,
            lamp_y + (beam_end.1 - lamp_y) * t,
        )
    };
    let beam: Vec<Point> = {
        let steps = 20;
        let edge = |i: usize, sign: f32| {
            let t = i as f32 / steps as f32;
            let (x, y) = beam_at(t);
            let close = 1.0 - smooth(0.75, 1.0, t) * 0.8;
            frame.pt(x, y + sign * (0.02 + 0.06 * t) * close)
        };
        (0..=steps)
            .map(|i| edge(i, -1.0))
            .chain((0..=steps).rev().map(|i| edge(i, 1.0)))
            .collect()
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
    let glint = reflection(
        &frame,
        lamp_x,
        (horizon + 0.008, 0.88),
        (0.034, 0.01),
        seed ^ 3,
    );
    let sea = lens.below(&frame, |_| horizon);
    let rows = |fine: usize, coarse: usize| if style.fine() { fine } else { coarse };

    let mut stages = Stages::new(style.ticks(STAGE_TICKS));
    stages.stage(|p| {
        let (height, top) = if style.dark() {
            (0.28, horizon - 0.24)
        } else {
            (0.5, horizon - 0.44)
        };
        p.mask(0.0, lens.sky(&frame, height, |_| horizon), 0.04);
        graded_sky(
            p,
            &frame,
            style,
            (lens.cx, lens.rx),
            (top, horizon - 0.015),
            rows(5, 3),
            &if style.dark() {
                [(cerulean, 0.1, 0.34), (phthalo, 0.0, 0.07)]
            } else {
                [(cerulean, 0.34, 0.12), (phthalo, 0.12, 0.0)]
            },
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        stencil_fill(
            p,
            &frame,
            style,
            (sea.clone(), 0.008),
            (0.0, 3.0, horizon, lens.deep),
            rows(8, 5),
            &[(phthalo, 0.34), (cerulean, 0.12)],
        );
        if style.fine() {
            streak(
                p,
                &frame,
                style,
                phthalo,
                (0.35, 1.55, horizon + 0.02),
                0.02,
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
            &[(rock, 0.65), (phthalo, 0.2)],
        );
        streak(
            p,
            &frame,
            style,
            phthalo,
            (1.86, 2.86, horizon - 0.012),
            0.02,
            0.6,
        );
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
                style.conc(1.0),
                style.water(0.35),
                0.5,
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
        if style.dark() {
            p.clear_mask(0.0);
        } else {
            lift_along(
                p,
                &frame,
                style,
                ((lamp_x - 0.04, lamp_y), beam_at(1.0)),
                (0.02, 0.075),
                0.8,
            );
            p.mask(0.0, beam.clone(), 0.03);
        }
        let (layer, reach) = if style.dark() {
            (0.12, 0.45)
        } else {
            (0.1, 1.0)
        };
        for t in [reach, reach * 0.75, reach * 0.5] {
            let (c, w) = style.glow(layer, 0.5);
            let (x, y) = beam_at(t);
            p.at(
                0.0,
                tapered(
                    vec![frame.pt(lamp_x - 0.04, lamp_y), frame.pt(x, y)],
                    (0.015, 0.015 + 0.07 * t),
                    gold,
                    c,
                    w,
                    0.95,
                ),
            );
        }
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        lit_reflection(
            p,
            &frame,
            style,
            gold,
            &glint,
            lamp_x,
            (horizon + 0.012, 0.86),
            (0.034, 0.012),
        );
        p.mask(0.0, sea.clone(), 0.008);
        for &(x0, x1, y) in &[
            (0.45, 0.85, 0.72),
            (1.1, 1.6, 0.79),
            (0.7, 1.0, 0.86),
            (2.45, 2.72, 0.74),
            (1.75, 2.05, 0.9),
        ] {
            streak(p, &frame, style, phthalo, (x0, x1, y), 0.008, 0.8);
        }
        p.settle(0.9, 2.5);
    });
    style.scene(
        "hub-lighthouse",
        palette,
        WIDE_3_1,
        Paper::hot_press(style.seed()),
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

/// A cabin on a lake shore under the moon: afterglow along the horizon, far
/// hills, the lake around the moon's reflection, the shore with the cabin
/// and pines, then the moon and the lit window, and last their light on the
/// water.
pub(super) fn lakeside_cabin(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_3_1);
    let indigo = role(palette, PigmentRole::BaseWash);
    let grey = role(palette, PigmentRole::Shadow);
    let silhouette = if style.dark() { indigo } else { grey };
    let rose = role(palette, PigmentRole::Accent);
    let gold = role(palette, PigmentRole::Glow);
    let seed = style.seed().0;
    let horizon = 0.57;
    let lens = Lens {
        cx: 1.5,
        rx: 1.42,
        rim: horizon,
        deep: 0.97,
        power: 2.2,
        wobble: 0.02,
        seed,
    };
    let moon = (0.8, 0.22, 0.08);
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
    let moon_glint = reflection(
        &frame,
        moon.0,
        (horizon + 0.006, 0.9),
        (0.07, 0.014),
        seed ^ 5,
    );
    let lamp_glint = reflection(
        &frame,
        window_x,
        (sill - 0.005, sill + 0.13),
        (0.028, 0.008),
        seed ^ 6,
    );
    let lake = lens.below(&frame, |_| horizon);
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
        let (height, top) = if style.dark() {
            (0.22, horizon - 0.18)
        } else {
            (0.36, horizon - 0.3)
        };
        p.mask(
            0.0,
            with_hole(
                lens.sky(&frame, height, |_| horizon),
                frame.circle(moon.0, moon.1, moon.2 + 0.006, 40),
            ),
            0.06,
        );
        graded_sky(
            p,
            &frame,
            style,
            (lens.cx, lens.rx),
            (top, horizon - 0.012),
            rows(5, 3),
            &[(indigo, 0.0, 0.16), (rose, 0.0, 0.34)],
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        stencil_fill(
            p,
            &frame,
            style,
            (
                lens.below(&frame, |x| hills.y(x)).into_iter().collect(),
                0.006,
            ),
            (0.0, 3.0, hills.crest() - 0.02, horizon + 0.02),
            rows(5, 3),
            &[(indigo, 0.34), (grey, 0.1)],
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        stencil_fill(
            p,
            &frame,
            style,
            (lake.clone(), 0.008),
            (0.0, 3.0, horizon, lens.deep),
            rows(8, 5),
            &[(indigo, 0.36)],
        );
        if style.fine() {
            streak(
                p,
                &frame,
                style,
                grey,
                (0.25, 1.45, horizon + 0.018),
                0.018,
                0.4,
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
                style.conc(1.0),
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
                    style.conc(1.1),
                    style.water(0.35),
                    0.6,
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
                style.conc(1.1),
                style.water(0.35),
                0.6,
            ),
        );
        p.settle(0.9, 2.5);
    });
    stages.stage(|p| {
        p.mask(0.0, frame.circle(moon.0, moon.1, moon.2, 40), 0.004);
        glow(p, &frame, style, gold, (moon.0, moon.1), moon.2 * 1.3, 1.3);
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
        for (glint, x, span, r) in [
            (&moon_glint, moon.0, (horizon + 0.01, 0.88), (0.065, 0.014)),
            (
                &lamp_glint,
                window_x,
                (sill + 0.004, sill + 0.12),
                (0.026, 0.008),
            ),
        ] {
            lit_reflection(p, &frame, style, gold, glint, x, span, r);
        }
        p.settle(0.9, 2.5);
    });
    style.scene(
        "hub-lakeside-cabin",
        palette,
        WIDE_3_1,
        Paper::hot_press(style.seed()),
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
