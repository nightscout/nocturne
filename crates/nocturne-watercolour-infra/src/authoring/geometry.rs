//! Geometry for the catalogue, in a design space whose `y` spans `0..1` and
//! whose `x` spans `0..aspect`. The simulation grid is square and is
//! stretched to the scene's size hint on output, so a polygon that should
//! read round has to be authored as an ellipse; [`Frame`] does that mapping
//! once. Stamp radii and feathers are already measured by the core in this
//! metric (`scene::isotropic_scale`), so they are passed through unchanged.

use std::f32::consts::TAU;

use nocturne_watercolour_core::domain::seed::hash2;
use nocturne_watercolour_core::domain::{Point, Seed, SizeHint};

#[derive(Debug, Clone, Copy)]
pub(super) struct Frame {
    pub aspect: f32,
}

impl Frame {
    pub fn new(size: SizeHint) -> Frame {
        Frame {
            aspect: size.width as f32 / size.height.max(1) as f32,
        }
    }

    /// Design-space point to normalised scene coordinates, clamped to the
    /// unit square (the validator rejects anything outside it).
    pub fn pt(&self, x: f32, y: f32) -> Point {
        Point::new((x / self.aspect).clamp(0.0, 1.0), y.clamp(0.0, 1.0))
    }

    pub fn map(&self, points: &[Point]) -> Vec<Point> {
        points.iter().map(|p| self.pt(p.x, p.y)).collect()
    }

    /// A circle of radius `r` design units as a closed polygon.
    pub fn circle(&self, cx: f32, cy: f32, r: f32, n: usize) -> Vec<Point> {
        (0..n)
            .map(|i| {
                let a = i as f32 / n as f32 * TAU;
                self.pt(cx + r * a.cos(), cy + r * a.sin())
            })
            .collect()
    }

    /// The same circle as a stroke path: the first point repeated so the
    /// brush closes the ring.
    pub fn ring(&self, cx: f32, cy: f32, r: f32, n: usize) -> Vec<Point> {
        let mut pts = self.circle(cx, cy, r, n);
        pts.push(pts[0]);
        pts
    }

    pub fn rect(&self, x0: f32, y0: f32, x1: f32, y1: f32) -> Vec<Point> {
        vec![
            self.pt(x0, y0),
            self.pt(x1, y0),
            self.pt(x1, y1),
            self.pt(x0, y1),
        ]
    }

    pub fn line(&self, x0: f32, y0: f32, x1: f32, y1: f32) -> Vec<Point> {
        vec![self.pt(x0, y0), self.pt(x1, y1)]
    }

    /// The path a flat wash is actually laid along: `rows` overlapping
    /// horizontal sweeps between `y0` and `y1`, alternating direction, joined
    /// at the turns into one continuous walk.
    ///
    /// This is how a wide area gets painted without a wide stamp. One fat
    /// stamp over a short path covers its whole footprint on the first step of
    /// the reveal, so there is nothing to watch; the same area hatched in
    /// takes the pen a path long enough to pace. The stencil still owns the
    /// silhouette — `x0` and `x1` are meant to sit *outside* the mask so the
    /// turns are clipped away and no row ends inside the shape.
    pub fn hatch(&self, x0: f32, x1: f32, y0: f32, y1: f32, rows: usize) -> Vec<Point> {
        let rows = rows.max(1);
        let mut path = Vec::with_capacity(rows * 2);
        for i in 0..rows {
            let t = if rows == 1 {
                0.5
            } else {
                i as f32 / (rows - 1) as f32
            };
            let y = y0 + (y1 - y0) * t;
            let (a, b) = if i % 2 == 0 { (x0, x1) } else { (x1, x0) };
            path.push(self.pt(a, y));
            path.push(self.pt(b, y));
        }
        path
    }

    /// The brush radius that makes [`Self::hatch`]'s rows merge into one wash
    /// rather than reading as stripes: a little over the row pitch, so a row
    /// overlaps its neighbour by more than half a brush and the tidelines
    /// between them close. A single row falls back to covering the whole band.
    pub fn hatch_radius(&self, y0: f32, y1: f32, rows: usize) -> f32 {
        let span = (y1 - y0).abs();
        if rows <= 1 {
            return (span * 0.5).max(1e-3);
        }
        (span / (rows - 1) as f32 * 1.1).max(1e-3)
    }

    /// A hill silhouette: `profile(x)` is the ridge height in design `y`
    /// across `x0..x1`; the polygon closes along `base_y`.
    pub fn ridge(
        &self,
        x0: f32,
        x1: f32,
        base_y: f32,
        samples: usize,
        profile: impl Fn(f32) -> f32,
    ) -> Vec<Point> {
        let mut pts: Vec<Point> = (0..samples)
            .map(|i| {
                let x = x0 + (x1 - x0) * i as f32 / (samples - 1).max(1) as f32;
                self.pt(x, profile(x))
            })
            .collect();
        pts.push(self.pt(x1, base_y));
        pts.push(self.pt(x0, base_y));
        pts
    }
}

/// Ridge profile: Gaussian bumps rising from `base_y`, with a seeded
/// low-frequency wobble so no two hills share a silhouette.
pub(super) struct Hills {
    pub base_y: f32,
    /// `(centre_x, half_width, height)` per bump, design units.
    pub bumps: Vec<(f32, f32, f32)>,
    pub wobble: f32,
    pub seed: Seed,
}

impl Hills {
    pub fn height(&self, x: f32) -> f32 {
        let mut rise = 0.0f32;
        for &(cx, hw, h) in &self.bumps {
            let d = (x - cx) / hw.max(1e-3);
            rise += h * (-d * d).exp();
        }
        let n = value_noise_1d(self.seed.0, x * 6.0) - 0.5;
        (self.base_y - rise - n * self.wobble).clamp(0.0, 1.0)
    }

    /// The same silhouette reflected about `base_y`, flattened by `squash`.
    pub fn reflected_height(&self, x: f32, squash: f32) -> f32 {
        let above = self.base_y - self.height(x);
        (self.base_y + above * squash).clamp(0.0, 1.0)
    }
}

/// Value noise along one axis in `0..1`, from the shared lattice hash.
pub(super) fn value_noise_1d(seed: u64, x: f32) -> f32 {
    let xi = x.floor();
    let f = x - xi;
    let s = f * f * (3.0 - 2.0 * f);
    let a = hash2(seed, xi as i32, 0);
    let b = hash2(seed, xi as i32 + 1, 0);
    a + (b - a) * s
}

pub(super) fn dist(a: Point, b: Point) -> f32 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2)).sqrt()
}

/// Crescent in design units: a disc with a second disc bitten out of it.
/// Outputs are unmapped; pass them through [`Frame::map`].
pub(super) struct Crescent {
    pub centre: Point,
    pub radius: f32,
    pub inner_centre: Point,
    pub inner_radius: f32,
}

impl Crescent {
    /// A crescent of radius `r` whose horns open toward `+x`; `thinness` in
    /// `0..1` moves from a fat crescent to a sliver.
    pub fn at(cx: f32, cy: f32, r: f32, thinness: f32) -> Crescent {
        Crescent {
            centre: Point::new(cx, cy),
            radius: r,
            inner_centre: Point::new(cx + r * (0.2 + 0.3 * thinness), cy - r * 0.15),
            inner_radius: r * (0.94 - 0.05 * thinness),
        }
    }

    pub fn outline(&self, samples: usize) -> Vec<Point> {
        let outer: Vec<Point> = (0..samples)
            .map(|i| {
                let a = i as f32 / samples as f32 * TAU;
                Point::new(
                    self.centre.x + self.radius * a.cos(),
                    self.centre.y + self.radius * a.sin(),
                )
            })
            .collect();
        let inner: Vec<Point> = (0..samples)
            .map(|i| {
                let a = i as f32 / samples as f32 * TAU;
                Point::new(
                    self.inner_centre.x + self.inner_radius * a.cos(),
                    self.inner_centre.y + self.inner_radius * a.sin(),
                )
            })
            .collect();
        let outside_inner = |p: &Point| dist(*p, self.inner_centre) >= self.inner_radius;
        let inside_outer = |p: &Point| dist(*p, self.centre) <= self.radius;
        let outer_arc = contiguous_run(&outer, outside_inner);
        let mut inner_arc = contiguous_run(&inner, inside_outer);
        inner_arc.reverse();
        let mut polygon = outer_arc;
        polygon.extend(inner_arc);
        polygon
    }

    fn outer_arc_len(&self, outline: &[Point]) -> usize {
        outline
            .iter()
            .take_while(|p| dist(**p, self.inner_centre) >= self.inner_radius - 1e-4)
            .count()
            .max(1)
    }

    fn push_out(&self, p: Point, offset: f32) -> Point {
        let scale = (self.radius + offset) / self.radius;
        Point::new(
            self.centre.x + (p.x - self.centre.x) * scale,
            self.centre.y + (p.y - self.centre.y) * scale,
        )
    }

    /// The outer arc pushed `offset` further from the centre, for a halo.
    pub fn outer_halo(&self, offset: f32, samples: usize) -> Vec<Point> {
        let outline = self.outline(256);
        let outer_len = self.outer_arc_len(&outline);
        (0..samples)
            .map(|i| {
                let t = i as f32 / (samples - 1) as f32;
                let o = outline[((t * (outer_len - 1) as f32).round() as usize).min(outer_len - 1)];
                self.push_out(o, offset)
            })
            .collect()
    }

    /// The crescent with its outer arc pushed out by up to `offset`, tapering
    /// to nothing at the horns so the tips keep their points; the inner arc
    /// is unchanged.
    pub fn mask_outline(&self, offset: f32, samples: usize) -> Vec<Point> {
        let outline = self.outline(samples);
        let outer_len = self.outer_arc_len(&outline);
        outline
            .iter()
            .enumerate()
            .map(|(i, p)| {
                if i < outer_len {
                    let t = i as f32 / (outer_len - 1).max(1) as f32;
                    self.push_out(*p, offset * (4.0 * t * (1.0 - t)).max(0.0).sqrt())
                } else {
                    *p
                }
            })
            .collect()
    }

    /// Centre line of the crescent body, from horn to horn.
    pub fn spine(&self, samples: usize) -> Vec<Point> {
        let outline = self.outline(256);
        let n = outline.len();
        let outer_len = self.outer_arc_len(&outline);
        let inner = &outline[outer_len..n];
        (0..samples)
            .map(|i| {
                let t = i as f32 / (samples - 1) as f32;
                let o =
                    &outline[((t * (outer_len - 1) as f32).round() as usize).min(outer_len - 1)];
                let inner_idx = (((1.0 - t) * (inner.len().max(1) - 1) as f32).round() as usize)
                    .min(inner.len().saturating_sub(1));
                let i_pt = inner.get(inner_idx).copied().unwrap_or(*o);
                Point::new((o.x + i_pt.x) * 0.5, (o.y + i_pt.y) * 0.5)
            })
            .collect()
    }

    /// The spine pulled toward the concave edge: `inset` is the fraction of
    /// the half-width kept between the points and the inner arc.
    pub fn concave_edge(&self, inset: f32, samples: usize) -> Vec<Point> {
        self.spine(samples)
            .iter()
            .map(|p| {
                let d = dist(*p, self.inner_centre).max(1e-5);
                let gap = d - self.inner_radius;
                let pull = (gap * (1.0 - inset)).max(0.0);
                Point::new(
                    p.x + (self.inner_centre.x - p.x) / d * pull,
                    p.y + (self.inner_centre.y - p.y) / d * pull,
                )
            })
            .collect()
    }
}

/// The single contiguous run (cyclically) of points satisfying `keep`.
fn contiguous_run(points: &[Point], keep: impl Fn(&Point) -> bool) -> Vec<Point> {
    let n = points.len();
    let start = (0..n)
        .find(|&i| !keep(&points[(i + n - 1) % n]) && keep(&points[i]))
        .unwrap_or(0);
    let mut run = Vec::new();
    for k in 0..n {
        let p = points[(start + k) % n];
        if keep(&p) {
            run.push(p);
        } else if !run.is_empty() {
            break;
        }
    }
    run
}

/// Bell silhouette: a dome that flares into a lip, closed across the lip's
/// underside. `cx` is the axis, `top` and `lip` the dome's top and the lip's
/// upper edge in design `y`, `lip_hw` the lip half-width.
pub(super) fn bell_outline(
    frame: &Frame,
    cx: f32,
    top: f32,
    lip: f32,
    lip_hw: f32,
    lip_depth: f32,
) -> Vec<Point> {
    let n = 26;
    // A hemispherical cap over the top 40 % of the height, sides that widen a
    // little, then the flare into the lip.
    let hw = |t: f32| {
        if t < 0.4 {
            let u = (0.4 - t) / 0.4;
            lip_hw * 0.62 * (1.0 - u * u).max(0.0).sqrt()
        } else {
            let s = (t - 0.4) / 0.6;
            lip_hw * (0.62 + 0.08 * s + 0.3 * s.powi(4))
        }
    };
    let right: Vec<(f32, f32)> = (0..=n)
        .map(|i| {
            let t = i as f32 / n as f32;
            (hw(t), top + (lip - top) * t)
        })
        .collect();
    let mut pts = Vec::with_capacity(2 * n + 6);
    pts.extend(right.iter().map(|&(w, y)| frame.pt(cx + w, y)));
    pts.push(frame.pt(cx + lip_hw * 1.04, lip + lip_depth * 0.5));
    pts.push(frame.pt(cx + lip_hw * 0.98, lip + lip_depth));
    pts.push(frame.pt(cx - lip_hw * 0.98, lip + lip_depth));
    pts.push(frame.pt(cx - lip_hw * 1.04, lip + lip_depth * 0.5));
    pts.extend(right.iter().rev().map(|&(w, y)| frame.pt(cx - w, y)));
    pts
}

/// A rectangle with a wedge cut down from its top edge under `notch_x`: the
/// water polygon whose gap is a moon's reflection. The wedge narrows from
/// `hw[0]` at the top to `hw[1]` at `notch_bottom`; its edges wobble by
/// `seed` so the gap reads as broken light rather than a cut.
/// A loose body of water: a straight top (the horizon stays crisp), sides
/// that wobble and lean inward, and an irregular bottom that stops short of
/// the frame, so the wash reads as a vignette rather than a crop.
pub(super) struct WaterBody {
    pub x0: f32,
    pub x1: f32,
    pub top: f32,
    pub bottom: f32,
    pub seed: Seed,
}

impl WaterBody {
    const SIDE_STEPS: usize = 6;
    const BOTTOM_STEPS: usize = 8;
    /// How far each side leans inward by the bottom, design units.
    const LEAN: f32 = 0.14;
    const WOBBLE: f32 = 0.05;

    fn wobble(&self, salt: u64, t: f32) -> f32 {
        (value_noise_1d(self.seed.0 ^ salt, t) - 0.5) * 2.0 * Self::WOBBLE
    }

    /// Right side, top to bottom.
    fn right(&self, frame: &Frame) -> Vec<Point> {
        (1..=Self::SIDE_STEPS)
            .map(|i| {
                let t = i as f32 / Self::SIDE_STEPS as f32;
                let x = self.x1 - Self::LEAN * t * t + self.wobble(0x51, t * 4.0);
                frame.pt(x, self.top + (self.bottom - self.top) * t)
            })
            .collect()
    }

    /// Bottom, right to left, rising unevenly.
    fn floor(&self, frame: &Frame) -> Vec<Point> {
        (1..Self::BOTTOM_STEPS)
            .map(|i| {
                let t = i as f32 / Self::BOTTOM_STEPS as f32;
                let x = self.x1 - Self::LEAN - (self.x1 - self.x0 - 2.0 * Self::LEAN) * t;
                let y = self.bottom - 0.02 - self.wobble(0x52, t * 4.0 + 0.5).abs() * 1.6;
                frame.pt(x, y)
            })
            .collect()
    }

    /// Left side, bottom up to exactly `(x0, to_y)`.
    fn left(&self, frame: &Frame, to_y: f32) -> Vec<Point> {
        (0..Self::SIDE_STEPS)
            .map(|i| {
                let t = 1.0 - i as f32 / Self::SIDE_STEPS as f32;
                let x = self.x0 + Self::LEAN * t * t + self.wobble(0x53, t * 4.0) * t;
                frame.pt(x, to_y + (self.bottom - to_y) * t)
            })
            .chain(std::iter::once(frame.pt(self.x0, to_y)))
            .collect()
    }

    /// A path just inside the bottom edge, left to right, for the damp
    /// brush that lifts the dried rim so the wash dissolves downward.
    pub fn floor_path(&self, frame: &Frame, inset: f32) -> Vec<Point> {
        let mut pts: Vec<Point> = self
            .floor(frame)
            .into_iter()
            .map(|p| Point::new(p.x, (p.y - inset).max(0.0)))
            .collect();
        pts.reverse();
        pts
    }

    /// Everything below the top edge, right side first; callers prepend
    /// their own top edge and may append more of the left side.
    pub fn below(&self, frame: &Frame, left_to_y: f32) -> Vec<Point> {
        let mut pts = self.right(frame);
        pts.extend(self.floor(frame));
        pts.extend(self.left(frame, left_to_y));
        pts
    }

    pub fn polygon(&self, frame: &Frame) -> Vec<Point> {
        let mut pts = vec![frame.pt(self.x0, self.top), frame.pt(self.x1, self.top)];
        pts.extend(self.below(frame, self.top));
        pts
    }

    /// The body with `notch` cut down from its top edge.
    pub fn with_notch(&self, frame: &Frame, notch: &Notch) -> Vec<Point> {
        let (left, right) = notch.edges(frame);
        let mut pts = vec![frame.pt(self.x0, self.top)];
        pts.extend(left);
        pts.extend(right.into_iter().rev());
        pts.push(frame.pt(self.x1, self.top));
        pts.extend(self.below(frame, self.top));
        pts
    }
}

pub(super) struct Notch {
    pub x: f32,
    pub top: f32,
    pub bottom: f32,
    /// Half-width at the top and at the bottom, design units.
    pub hw: [f32; 2],
    pub seed: Seed,
}

impl Notch {
    /// Left and right edges, each top to bottom.
    fn edges(&self, frame: &Frame) -> (Vec<Point>, Vec<Point>) {
        let steps = 9;
        let edge = |side: f32| -> Vec<Point> {
            (0..=steps)
                .map(|i| {
                    let t = i as f32 / steps as f32;
                    let y = self.top + (self.bottom - self.top) * t;
                    let half = self.hw[0] + (self.hw[1] - self.hw[0]) * t;
                    let wob = (value_noise_1d(self.seed.0 ^ u64::from(side > 0.0), t * 5.0 + 0.3)
                        - 0.5)
                        * half;
                    frame.pt(self.x + side * half + wob, y)
                })
                .collect()
        };
        (edge(-1.0), edge(1.0))
    }

    /// The wedge itself, as a closed polygon.
    pub fn wedge(&self, frame: &Frame) -> Vec<Point> {
        let (left, right) = self.edges(frame);
        let mut pts = left;
        pts.extend(right.into_iter().rev());
        pts
    }
}
