//! Rasterising operations onto the simulation grid.
//!
//! Strokes become a [`Stamp`] (per-cell coverage in `0..1`) computed once on
//! the CPU and then added into the grid. Backends share this code, so the
//! shader port only has to add a coverage field rather than mirror the
//! geometry. Coverage is perturbed by paper height (high grain resists paint,
//! so a wash edge breaks up on rough paper) and by a small seeded jitter of
//! the radius along the path.

use super::grid::SimulationGrid;
use super::ops::{BrushStroke, LiftStroke, Mask, Point, RadiusProfile, StrokeSpan, WaterStroke};
use super::scene::isotropic_scale;
use super::seed::{Seed, hash2};

#[derive(Debug, Clone, PartialEq)]
pub struct Stamp {
    pub width: u32,
    pub height: u32,
    pub coverage: Vec<f32>,
}

/// Paper-driven edge break-up and radius jitter, as fractions of the radius.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StampParams {
    pub edge_roughness: f32,
    pub jitter: f32,
}

impl Default for StampParams {
    fn default() -> Self {
        StampParams {
            edge_roughness: 0.45,
            jitter: 0.08,
        }
    }
}

/// How hard a moving brush pushes the water. See [`StrokeFlow`].
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FlowParams {
    /// Cells per tick of travel-direction velocity per unit of path walked,
    /// where 1.0 of path is the canvas edge.
    pub kick: f32,
    /// Ceiling on the kick, well inside `max_velocity` so a long sweep cannot
    /// saturate the advection.
    pub kick_max: f32,
    /// Cells per tick of outward velocity per unit of water laid.
    pub splat_out: f32,
}

impl Default for FlowParams {
    fn default() -> Self {
        FlowParams {
            kick: 0.6,
            kick_max: 0.3,
            splat_out: 0.25,
        }
    }
}

/// The velocity a laydown injects into the water it lands in.
///
/// Paint that came from a moving brush does not sit where it was put: the tip
/// drags the film with it, and the landing water shoulders the rest aside.
/// Without this a stroke is a stamp whose edges then bleed, which is the
/// difference between ink settling and a brush being drawn. Sudo Aquarelle,
/// the reference this simulation follows, injects both terms on every splat.
///
/// Both are in cells per tick at full coverage, the units of
/// [`super::sim::SimParams::max_velocity`], and both are scaled by the cell
/// coverage so they vanish outside the mark.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct StrokeFlow {
    /// Along the direction the tip travelled over this laydown.
    pub kick: (f32, f32),
    /// Away from the centre of the mark, magnitude only; the direction is
    /// read off the coverage field at each cell.
    pub splat_out: f32,
}

/// The point on `path` at arc-length fraction `t`, in the isotropic metric the
/// rasteriser measures in. Shares that walk exactly, so the chord a span
/// reports is the chord the span actually laid.
fn point_at(path: &[Point], aspect: f32, t: f32) -> (f32, f32) {
    let (ax, ay) = isotropic_scale(aspect);
    let iso = |p: &Point| (p.x * ax, p.y * ay);
    match path {
        [] => (0.0, 0.0),
        [only] => iso(only),
        _ => {
            let segs: Vec<((f32, f32), (f32, f32))> =
                path.windows(2).map(|w| (iso(&w[0]), iso(&w[1]))).collect();
            let lens: Vec<f32> = segs
                .iter()
                .map(|(a, b)| ((b.0 - a.0).powi(2) + (b.1 - a.1).powi(2)).sqrt())
                .collect();
            let total: f32 = lens.iter().sum();
            if total <= 0.0 {
                return segs[0].0;
            }
            let mut want = t.clamp(0.0, 1.0) * total;
            for ((a, b), len) in segs.iter().zip(&lens) {
                if want <= *len {
                    let f = if *len > 0.0 { want / len } else { 0.0 };
                    return (a.0 + (b.0 - a.0) * f, a.1 + (b.1 - a.1) * f);
                }
                want -= len;
            }
            segs[segs.len() - 1].1
        }
    }
}

/// The flow a laydown of `span` along `path` injects, given the water it lays.
///
/// The kick is capped at `kick_max` so a span crossing the whole canvas does
/// not fling the sheet, and a dab, whose chord is zero, gets none at all —
/// a dab has no direction, only an outward push.
pub fn stroke_flow(
    path: &[Point],
    span: StrokeSpan,
    aspect: f32,
    water: f32,
    params: FlowParams,
) -> StrokeFlow {
    let a = point_at(path, aspect, span.start);
    let b = point_at(path, aspect, span.end);
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    let travelled = (dx * dx + dy * dy).sqrt();
    let kick = if travelled > 1e-6 {
        let mag = (params.kick * travelled).min(params.kick_max);
        (dx / travelled * mag, dy / travelled * mag)
    } else {
        (0.0, 0.0)
    };
    StrokeFlow {
        kick,
        splat_out: params.splat_out * water.max(0.0),
    }
}

/// The unit vector pointing out of the mark at cell `i`: coverage is highest
/// on the centre line and falls to zero at the rim, so outward is the descent
/// direction of the coverage field. Reading it off the stamp rather than off
/// the stroke geometry gets curved paths, tapered radii and jittered rims
/// right for free, and needs nothing extra uploaded to the GPU.
fn outward_at(stamp: &Stamp, i: usize) -> (f32, f32) {
    let (w, h) = (stamp.width as usize, stamp.height as usize);
    if w == 0 || h == 0 {
        return (0.0, 0.0);
    }
    let (x, y) = (i % w, i / w);
    let l = x.saturating_sub(1);
    let r = (x + 1).min(w - 1);
    let up = y.saturating_sub(1);
    let dn = (y + 1).min(h - 1);
    let gx = stamp.coverage[y * w + r] - stamp.coverage[y * w + l];
    let gy = stamp.coverage[dn * w + x] - stamp.coverage[up * w + x];
    let len = (gx * gx + gy * gy).sqrt();
    if len <= 1e-6 {
        (0.0, 0.0)
    } else {
        (-gx / len, -gy / len)
    }
}

/// Adds a laydown flow at one cell. Shared by brush and water so the two
/// cannot drift apart, and mirrored in `apply.wgsl`.
#[inline]
fn inject_flow(grid: &mut SimulationGrid, stamp: &Stamp, i: usize, cov: f32, flow: StrokeFlow) {
    let (ox, oy) = if flow.splat_out != 0.0 {
        outward_at(stamp, i)
    } else {
        (0.0, 0.0)
    };
    grid.velocity_u[i] += cov * (flow.kick.0 + flow.splat_out * ox);
    grid.velocity_v[i] += cov * (flow.kick.1 + flow.splat_out * oy);
}

/// Cells with coverage above this become wet when a stroke lands.
pub const WET_THRESHOLD: f32 = 1e-3;

/// How much paper height modulates the water a stroke lays down: a cell at
/// height `h` receives `1 + GAIN * (0.5 - h) * 2` times the nominal water, so
/// valleys start deeper and pooling begins at the stroke itself.
pub const STROKE_WATER_PAPER_GAIN: f32 = 0.5;

pub fn stroke_water_factor(paper_height: f32) -> f32 {
    (1.0 + STROKE_WATER_PAPER_GAIN * (0.5 - paper_height) * 2.0).max(0.0)
}

/// The grid a stamp is rasterised for, with its paper height for edge break-up.
#[derive(Debug, Clone, Copy)]
pub struct StampTarget<'a> {
    pub width: u32,
    pub height: u32,
    pub paper_height: &'a [f32],
}

/// [`rasterize_path_aspect`] for a square output.
pub fn rasterize_path(
    path: &[Point],
    radius: RadiusProfile,
    softness: f32,
    target: StampTarget<'_>,
    seed: Seed,
    params: StampParams,
) -> Stamp {
    rasterize_path_aspect(path, radius, softness, target, 1.0, seed, params)
}

/// Rasterises a capsule stroke along `path`. Positions are normalised scene
/// coordinates; `radius` (and the paper-driven edge shift) are measured in
/// the isotropic metric of an output with `aspect` (see
/// [`isotropic_scale`]), so a stamp is round after the grid is stretched.
pub fn rasterize_path_aspect(
    path: &[Point],
    radius: RadiusProfile,
    softness: f32,
    target: StampTarget<'_>,
    aspect: f32,
    seed: Seed,
    params: StampParams,
) -> Stamp {
    rasterize_path_span(
        path,
        radius,
        softness,
        target,
        aspect,
        seed,
        params,
        StrokeSpan::FULL,
    )
}

/// The coverage the brush adds while its tip travels from arc-length fraction
/// `span.start` to `span.end`: `max(0, cov_upto(end) - cov_upto(start))` per
/// cell, where `cov_upto(t)` is the capsule-chain coverage of the path clipped
/// at fraction `t`. Radius, jitter and paper edge shift stay parameterised by
/// the full path in both terms, so consecutive spans of one stroke join
/// without seams and their union equals the full stamp.
#[allow(clippy::too_many_arguments)]
pub fn rasterize_path_span(
    path: &[Point],
    radius: RadiusProfile,
    softness: f32,
    target: StampTarget<'_>,
    aspect: f32,
    seed: Seed,
    params: StampParams,
    span: StrokeSpan,
) -> Stamp {
    let chain = Chain::new(path, radius, softness, target, aspect, seed, params);
    let (start, end) = (span.start.clamp(0.0, 1.0), span.end.clamp(0.0, 1.0));
    let coverage = match &chain {
        Some(chain) if span.start > 0.0 && start <= end => chain.span_coverage(start, end),
        _ => {
            let mut coverage = coverage_upto(chain.as_ref(), target, span.end);
            if span.start > 0.0 {
                let prefix = coverage_upto(chain.as_ref(), target, span.start);
                for (c, &p) in coverage.iter_mut().zip(&prefix) {
                    *c = (*c - p).max(0.0);
                }
            }
            coverage
        }
    };
    Stamp {
        width: target.width,
        height: target.height,
        coverage,
    }
}

/// Capsule-chain coverage of `path` clipped at arc-length fraction `t_end`.
/// A segment the clip crosses is walked only up to the clip point; `t` fed to
/// the radius profile and jitter hash is always the full path's
/// parameterisation of the cell's closest point, never the clip position, so
/// `cov_upto` is monotone in `t_end` and the two terms of a span subtract
/// exactly. A path with no arc length (a single point, or coincident points)
/// is a dab that appears once `t_end > 0`.
fn coverage_upto(chain: Option<&Chain<'_>>, target: StampTarget<'_>, t_end: f32) -> Vec<f32> {
    let n = (target.width as usize) * (target.height as usize);
    let mut coverage = vec![0.0f32; n];
    let t_end = t_end.clamp(0.0, 1.0);
    let Some(chain) = chain else {
        return coverage;
    };
    if t_end <= 0.0 {
        return coverage;
    }
    let full = Rect::new(0, target.width, 0, target.height);
    for si in 0..chain.segs.len() {
        chain.walk_into(si, t_end, full, None, &mut coverage);
    }
    coverage
}

/// A cell rectangle, `x0..x1` by `y0..y1`.
#[derive(Debug, Clone, Copy, PartialEq)]
struct Rect {
    x0: u32,
    x1: u32,
    y0: u32,
    y1: u32,
}

impl Rect {
    const EMPTY: Rect = Rect {
        x0: u32::MAX,
        x1: 0,
        y0: u32::MAX,
        y1: 0,
    };

    fn new(x0: u32, x1: u32, y0: u32, y1: u32) -> Rect {
        Rect { x0, x1, y0, y1 }
    }

    fn is_empty(&self) -> bool {
        self.x0 >= self.x1 || self.y0 >= self.y1
    }

    fn union(self, o: Rect) -> Rect {
        if o.is_empty() {
            return self;
        }
        Rect::new(
            self.x0.min(o.x0),
            self.x1.max(o.x1),
            self.y0.min(o.y0),
            self.y1.max(o.y1),
        )
    }

    fn intersect(self, o: Rect) -> Rect {
        Rect::new(
            self.x0.max(o.x0),
            self.x1.min(o.x1),
            self.y0.max(o.y0),
            self.y1.min(o.y1),
        )
    }

    fn width(&self) -> usize {
        self.x1.saturating_sub(self.x0) as usize
    }

    fn height(&self) -> usize {
        self.y1.saturating_sub(self.y0) as usize
    }

    fn has_row(&self, y: u32) -> bool {
        self.x0 < self.x1 && (self.y0..self.y1).contains(&y)
    }

    fn contains(&self, x: u32, y: u32) -> bool {
        self.has_row(y) && (self.x0..self.x1).contains(&x)
    }
}

/// One capsule of the chain, with the arc-length fractions of the full path
/// it runs between.
#[derive(Debug, Clone, Copy)]
struct Seg {
    a: Point,
    b: Point,
    t0: f32,
    t1: f32,
}

/// How a segment enters `cov_upto(t_end)`.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Walk {
    Skip,
    Full,
    /// Walked only up to this clip point.
    Clip(Point),
}

/// A stroke's segments in the isotropic metric, and what every cell of its
/// walk reads.
struct Chain<'a> {
    segs: Vec<Seg>,
    width: u32,
    height: u32,
    w: f32,
    h: f32,
    ax: f32,
    ay: f32,
    inner: f32,
    r_max: f32,
    cell: f32,
    radius: RadiusProfile,
    params: StampParams,
    seed: Seed,
    paper_height: &'a [f32],
}

impl<'a> Chain<'a> {
    /// `None` for an empty path, which covers nothing.
    fn new(
        path: &[Point],
        radius: RadiusProfile,
        softness: f32,
        target: StampTarget<'a>,
        aspect: f32,
        seed: Seed,
        params: StampParams,
    ) -> Option<Chain<'a>> {
        if path.is_empty() {
            return None;
        }
        let (w, h) = (target.width as f32, target.height as f32);
        let (ax, ay) = isotropic_scale(aspect);
        let iso = |p: &Point| Point::new(p.x * ax, p.y * ay);
        let segments: Vec<(Point, Point)> = if path.len() == 1 {
            vec![(iso(&path[0]), iso(&path[0]))]
        } else {
            path.windows(2).map(|p| (iso(&p[0]), iso(&p[1]))).collect()
        };
        let total_len: f32 = segments
            .iter()
            .map(|(a, b)| ((b.x - a.x).powi(2) + (b.y - a.y).powi(2)).sqrt())
            .sum::<f32>()
            .max(1e-6);
        let mut walked = 0.0f32;
        let segs = segments
            .iter()
            .map(|&(a, b)| {
                let seg_len = ((b.x - a.x).powi(2) + (b.y - a.y).powi(2)).sqrt();
                let t0 = walked / total_len;
                let t1 = (walked + seg_len) / total_len;
                walked += seg_len;
                Seg { a, b, t0, t1 }
            })
            .collect();
        Some(Chain {
            segs,
            width: target.width,
            height: target.height,
            w,
            h,
            ax,
            ay,
            inner: 1.0 - softness.clamp(0.0, 1.0),
            r_max: radius.max().max(1e-4) * (1.0 + params.jitter + params.edge_roughness),
            // A cell's size in the isotropic metric, for the anti-aliasing ramp.
            cell: (ax / w).max(ay / h),
            radius,
            params,
            seed,
            paper_height: target.paper_height,
        })
    }

    /// Only a partial span clips its final segment; a full span walks every
    /// segment with the unclipped formula, so `StrokeSpan::FULL` stays
    /// bit-identical to a single stamp.
    fn walk(&self, si: usize, t_end: f32) -> Walk {
        let s = self.segs[si];
        if s.t0 >= t_end {
            Walk::Skip
        } else if t_end >= 1.0 || s.t1 <= t_end {
            Walk::Full
        } else {
            let frac = (t_end - s.t0) / (s.t1 - s.t0).max(1e-12);
            Walk::Clip(Point::new(
                s.a.x + (s.b.x - s.a.x) * frac,
                s.a.y + (s.b.y - s.a.y) * frac,
            ))
        }
    }

    /// The segment's bounding box in grid cells: the isotropic radius spans
    /// r/ax of the width and r/ay of the height.
    fn rect(&self, si: usize, walk: Walk) -> Rect {
        let s = self.segs[si];
        let end = match walk {
            Walk::Skip => return Rect::EMPTY,
            Walk::Full => s.b,
            Walk::Clip(c) => c,
        };
        let (a, r_max, ax, ay, w, h) = (s.a, self.r_max, self.ax, self.ay, self.w, self.h);
        Rect::new(
            (((a.x.min(end.x) - r_max) / ax * w).floor().max(0.0)) as u32,
            (((a.x.max(end.x) + r_max) / ax * w).ceil().min(w)) as u32,
            (((a.y.min(end.y) - r_max) / ay * h).floor().max(0.0)) as u32,
            (((a.y.max(end.y) + r_max) / ay * h).ceil().min(h)) as u32,
        )
    }

    /// Cell `(x, y)`'s centre in the isotropic metric, its distance to
    /// segment `si` and the full-path `t` of the closest point.
    fn locate(&self, si: usize, x: u32, y: u32) -> (f32, f32, f32, f32) {
        let Seg { a, b, t0, t1 } = self.segs[si];
        let pu = (x as f32 + 0.5) / self.w * self.ax;
        let pv = (y as f32 + 0.5) / self.h * self.ay;
        let (dist, along) = distance_to_segment(pu, pv, a, b);
        (pu, pv, dist, t0 + (t1 - t0) * along)
    }

    /// The jittered radius at `t` on segment `si`.
    fn radius_at(&self, si: usize, t: f32) -> f32 {
        let jitter = 1.0
            + self.params.jitter * (hash2(self.seed.0, (t * 64.0) as i32, si as i32) * 2.0 - 1.0);
        (self.radius.at(t) * jitter).max(1e-5)
    }

    /// The distance `walk` measures: a clipped segment keeps each cell's
    /// full-path `t`, so the clip cap and the unclipped walk share one
    /// radius and jitter.
    fn walk_dist(walk: Walk, (pu, pv, dist, t): (f32, f32, f32, f32), t_end: f32) -> f32 {
        match walk {
            Walk::Clip(c) => {
                let cap_dist = ((pu - c.x).powi(2) + (pv - c.y).powi(2)).sqrt();
                if t <= t_end { dist } else { cap_dist }
            }
            _ => dist,
        }
    }

    fn cover(&self, r: f32, x: u32, y: u32, dist: f32) -> f32 {
        let idx = (y as usize) * (self.width as usize) + x as usize;
        let grain = self.paper_height.get(idx).copied().unwrap_or(0.5) - 0.5;
        let shifted = dist + grain * self.params.edge_roughness * r;
        let aa = 0.75 * self.cell / r;
        falloff(shifted / r, self.inner.min(1.0 - aa))
    }

    /// Takes the max of segment `si`'s coverage in `cov_upto(t_end)` into
    /// `buf`, which holds the cells of `frame`, over the part of its box
    /// inside `frame` and, per row of `frame`, inside `rows`.
    fn walk_into(
        &self,
        si: usize,
        t_end: f32,
        frame: Rect,
        rows: Option<&[(u32, u32)]>,
        buf: &mut [f32],
    ) {
        let walk = self.walk(si, t_end);
        let r = self.rect(si, walk).intersect(frame);
        let stride = frame.width();
        for y in r.y0..r.y1 {
            let ly = (y - frame.y0) as usize;
            let (lo, hi) = rows.map_or((r.x0, r.x1), |rows| rows[ly]);
            for x in r.x0.max(lo)..r.x1.min(hi) {
                let at = self.locate(si, x, y);
                let radius = self.radius_at(si, at.3);
                let cov = self.cover(radius, x, y, Self::walk_dist(walk, at, t_end));
                let local = ly * stride + (x - frame.x0) as usize;
                if cov > buf[local] {
                    buf[local] = cov;
                }
            }
        }
    }

    /// [`Chain::walk_into`] for both terms of a span at once, sharing each
    /// cell's geometry.
    #[allow(clippy::too_many_arguments)]
    fn walk_pair(
        &self,
        si: usize,
        (start, end): (f32, f32),
        frame: Rect,
        rows: &[(u32, u32)],
        upto_start: &mut [f32],
        upto_end: &mut [f32],
    ) {
        let (ws, we) = (self.walk(si, start), self.walk(si, end));
        let (rs, re) = (
            self.rect(si, ws).intersect(frame),
            self.rect(si, we).intersect(frame),
        );
        let both = Rect::EMPTY.union(rs).union(re);
        let stride = frame.width();
        for y in both.y0..both.y1 {
            let ly = (y - frame.y0) as usize;
            let (lo, hi) = rows[ly];
            for x in both.x0.max(lo)..both.x1.min(hi) {
                let (in_s, in_e) = (rs.contains(x, y), re.contains(x, y));
                if !in_s && !in_e {
                    continue;
                }
                let at = self.locate(si, x, y);
                let radius = self.radius_at(si, at.3);
                let local = ly * stride + (x - frame.x0) as usize;
                let mut from_start = None;
                if in_s {
                    let dist = Self::walk_dist(ws, at, start);
                    let cov = self.cover(radius, x, y, dist);
                    from_start = Some((dist, cov));
                    if cov > upto_start[local] {
                        upto_start[local] = cov;
                    }
                }
                if in_e {
                    let dist = Self::walk_dist(we, at, end);
                    let cov = match from_start {
                        Some((d, cov)) if d.to_bits() == dist.to_bits() => cov,
                        _ => self.cover(radius, x, y, dist),
                    };
                    if cov > upto_end[local] {
                        upto_end[local] = cov;
                    }
                }
            }
        }
    }

    /// The cells of row `y` in `x0..x1` whose closest point on segment `si`
    /// has `t <= t_end`. Every step from a cell's column to its `t` is
    /// monotone, rounding included, so they are one run at the end of the
    /// row the segment starts from.
    fn behind(&self, si: usize, y: u32, (x0, x1): (u32, u32), t_end: f32) -> (u32, u32) {
        if x0 >= x1 {
            return (x0, x0);
        }
        let Seg { a, b, .. } = self.segs[si];
        let (abx, aby) = (b.x - a.x, b.y - a.y);
        let settled = |x: u32| self.locate(si, x, y).3 <= t_end;
        let first = |pred: &dyn Fn(u32) -> bool| {
            let (mut lo, mut hi) = (x0, x1);
            while lo < hi {
                let mid = lo + (hi - lo) / 2;
                if pred(mid) { hi = mid } else { lo = mid + 1 }
            }
            lo
        };
        if abx * abx + aby * aby < 1e-12 || abx == 0.0 {
            if settled(x0) { (x0, x1) } else { (x0, x0) }
        } else if abx > 0.0 {
            (x0, first(&|x| !settled(x)))
        } else {
            (first(&settled), x1)
        }
    }

    /// Widens `rows` over the cells where segment `si` can add a different
    /// value to the two terms of a span. The rest of its box it adds to
    /// both alike: cells behind the start's clip point, which both terms
    /// measure to the segment itself.
    fn mark_differing(
        &self,
        si: usize,
        (start, end): (f32, f32),
        frame: Rect,
        rows: &mut [(u32, u32)],
    ) {
        let (ws, we) = (self.walk(si, start), self.walk(si, end));
        let (rs, re) = (
            self.rect(si, ws).intersect(frame),
            self.rect(si, we).intersect(frame),
        );
        let both = Rect::EMPTY.union(rs).union(re);
        for y in both.y0..both.y1 {
            let (s_row, e_row) = (rs.has_row(y), re.has_row(y));
            let mut span = (u32::MAX, 0u32);
            for (on, r) in [(s_row, rs), (e_row, re)] {
                if on {
                    span = (span.0.min(r.x0), span.1.max(r.x1));
                }
            }
            if span.0 >= span.1 {
                continue;
            }
            let same = if matches!(ws, Walk::Clip(_)) && s_row && e_row {
                self.behind(si, y, (rs.x0.max(re.x0), rs.x1.min(re.x1)), start)
            } else {
                (0, 0)
            };
            let differing = if same.0 >= same.1 {
                span
            } else if same.0 <= span.0 && same.1 >= span.1 {
                continue;
            } else if same.0 <= span.0 {
                (same.1, span.1)
            } else if same.1 >= span.1 {
                (span.0, same.0)
            } else {
                span
            };
            let row = &mut rows[(y - frame.y0) as usize];
            *row = (row.0.min(differing.0), row.1.max(differing.1));
        }
    }

    /// `max(0, cov_upto(end) - cov_upto(start))` for `0 < start <= end`,
    /// equal to the bit to subtracting the two full walks. Each term is a max
    /// over its segments, and a segment adds the same value to both terms
    /// wherever it walks both unclipped, or reaches a cell behind the start's
    /// clip point. Where every segment does, the difference is exactly zero,
    /// so only the cells where some segment differs are walked, the shared
    /// segments once for both terms: a span costs about its own length
    /// rather than the path's prefix.
    fn span_coverage(&self, start: f32, end: f32) -> Vec<f32> {
        let full = Rect::new(0, self.width, 0, self.height);
        let mut out = vec![0.0f32; full.width() * full.height()];
        let (shared, tail): (Vec<usize>, Vec<usize>) = (0..self.segs.len()).partition(|&si| {
            self.walk(si, start) == Walk::Full && self.walk(si, end) == Walk::Full
        });
        let mut frame = Rect::EMPTY;
        for &si in &tail {
            frame = frame
                .union(self.rect(si, self.walk(si, start)))
                .union(self.rect(si, self.walk(si, end)));
        }
        let frame = frame.intersect(full);
        if frame.is_empty() {
            return out;
        }
        let mut rows = vec![(u32::MAX, 0u32); frame.height()];
        for &si in &tail {
            self.mark_differing(si, (start, end), frame, &mut rows);
        }
        let mut upto_end = vec![0.0f32; frame.width() * frame.height()];
        for &si in &shared {
            self.walk_into(si, end, frame, Some(&rows), &mut upto_end);
        }
        let mut upto_start = upto_end.clone();
        for &si in &tail {
            self.walk_pair(
                si,
                (start, end),
                frame,
                &rows,
                &mut upto_start,
                &mut upto_end,
            );
        }
        let stride = frame.width();
        for (ly, &(lo, hi)) in rows.iter().enumerate() {
            if lo >= hi {
                continue;
            }
            let y = frame.y0 as usize + ly;
            let (a, b) = ((lo - frame.x0) as usize, (hi - frame.x0) as usize);
            let row = y * full.width() + frame.x0 as usize;
            let local = ly * stride;
            for (o, (&c, &p)) in out[row + a..row + b].iter_mut().zip(
                upto_end[local + a..local + b]
                    .iter()
                    .zip(&upto_start[local + a..local + b]),
            ) {
                *o = (c - p).max(0.0);
            }
        }
        out
    }
}

fn falloff(x: f32, inner: f32) -> f32 {
    if x <= inner {
        1.0
    } else if x >= 1.0 {
        0.0
    } else {
        let t = (x - inner) / (1.0 - inner).max(1e-5);
        1.0 - t * t * (3.0 - 2.0 * t)
    }
}

/// Distance from `(px, py)` to segment `ab` and the parameter along it.
fn distance_to_segment(px: f32, py: f32, a: Point, b: Point) -> (f32, f32) {
    let abx = b.x - a.x;
    let aby = b.y - a.y;
    let len2 = abx * abx + aby * aby;
    let t = if len2 < 1e-12 {
        0.0
    } else {
        (((px - a.x) * abx + (py - a.y) * aby) / len2).clamp(0.0, 1.0)
    };
    let cx = a.x + abx * t;
    let cy = a.y + aby * t;
    (((px - cx).powi(2) + (py - cy).powi(2)).sqrt(), t)
}

/// [`rasterize_mask_aspect`] for a square output.
pub fn rasterize_mask(mask: &Mask, width: u32, height: u32) -> Vec<f32> {
    rasterize_mask_aspect(mask, width, height, 1.0)
}

/// Bleed mask field for `Operation::SetMask`; see [`Mask`]. The inside test
/// uses normalised coordinates; feather and path radius are measured in the
/// isotropic metric of an output with `aspect` (see [`isotropic_scale`]).
pub fn rasterize_mask_aspect(mask: &Mask, width: u32, height: u32, aspect: f32) -> Vec<f32> {
    let n = (width as usize) * (height as usize);
    let mut best = vec![f32::MAX; n];
    let (w, h) = (width as f32, height as f32);
    let (ax, ay) = isotropic_scale(aspect);
    let iso_points: Vec<Point> = mask
        .points()
        .iter()
        .map(|p| Point::new(p.x * ax, p.y * ay))
        .collect();
    let feather = mask.feather();
    let radius = match mask {
        Mask::Polygon { .. } => 0.0,
        Mask::Path { radius, .. } => *radius,
    };
    // A cell farther than `feather + radius` from every segment is `0`: its
    // `outside` (`dist`, or `dist - radius` for a `Path`) already exceeds
    // `feather`, so the falloff is saturated. Walking each segment only over
    // its bounding box expanded by that band still records the exact
    // per-cell minimum wherever it can matter, because the closest segment to
    // a cell within the band always covers it.
    let band = feather + radius;
    for si in 0..iso_points.len().saturating_sub(1) {
        walk_mask_segment(
            &mut best,
            iso_points[si],
            iso_points[si + 1],
            band,
            w,
            h,
            width,
            ax,
            ay,
        );
    }
    match iso_points.len() {
        1 => walk_mask_segment(
            &mut best,
            iso_points[0],
            iso_points[0],
            band,
            w,
            h,
            width,
            ax,
            ay,
        ),
        0 => {}
        _ if matches!(mask, Mask::Polygon { .. }) => walk_mask_segment(
            &mut best,
            iso_points[iso_points.len() - 1],
            iso_points[0],
            band,
            w,
            h,
            width,
            ax,
            ay,
        ),
        _ => {}
    }
    // The even-odd inside test, pre-bucketed per row: an edge crosses only the
    // rows its y-range spans, so a cell casts against just those edges and a
    // full-canvas polygon is O(cells) instead of O(cells x points). The bucket
    // uses the same strict comparison the cast does, so each cell still toggles
    // on exactly the edges the unsplit test would.
    let row_edges: Vec<Vec<(Point, Point)>> = match mask {
        Mask::Polygon { points, .. } if points.len() >= 3 => {
            let mut rows = vec![Vec::new(); height as usize];
            for i in 0..points.len() {
                let a = points[i];
                let b = points[(i + points.len() - 1) % points.len()];
                for y in 0..height {
                    let py = (y as f32 + 0.5) / h;
                    if (a.y > py) != (b.y > py) {
                        rows[y as usize].push((a, b));
                    }
                }
            }
            rows
        }
        // A one- or two-point polygon still passes the bbox test (its points
        // and segment have a bounding box), so every row must exist or the
        // per-cell cast below indexes out of bounds and panics.
        _ => vec![Vec::new(); height as usize],
    };
    let mut out = vec![0.0f32; n];
    for y in 0..height {
        for x in 0..width {
            let pu = (x as f32 + 0.5) / w;
            let pv = (y as f32 + 0.5) / h;
            let idx = (y as usize) * (width as usize) + x as usize;
            let outside = match mask {
                Mask::Polygon { points, .. } => {
                    // A cell outside the polygon's bounding box is never
                    // inside, so the cast can be skipped there.
                    let inside = if in_polygon_bbox(pu, pv, points) {
                        let mut inside = false;
                        for &(a, b) in &row_edges[y as usize] {
                            let x_at = a.x + (pv - a.y) / (b.y - a.y) * (b.x - a.x);
                            if pu < x_at {
                                inside = !inside;
                            }
                        }
                        inside
                    } else {
                        false
                    };
                    if inside { 0.0 } else { best[idx] }
                }
                Mask::Path { radius, .. } => (best[idx] - radius).max(0.0),
            };
            let m = if outside <= 0.0 {
                1.0
            } else if feather <= 1e-6 {
                0.0
            } else {
                let t = (outside / feather).min(1.0);
                1.0 - t * t * (3.0 - 2.0 * t)
            };
            out[idx] = m;
        }
    }
    out
}

/// Records the exact distance to segment `ab` in every cell its bounding box,
/// expanded by `band` in the isotropic metric, covers.
#[allow(clippy::too_many_arguments)]
fn walk_mask_segment(
    best: &mut [f32],
    a: Point,
    b: Point,
    band: f32,
    w: f32,
    h: f32,
    width: u32,
    ax: f32,
    ay: f32,
) {
    let x_min = (((a.x.min(b.x) - band) / ax * w).floor().max(0.0)) as u32;
    let x_max = (((a.x.max(b.x) + band) / ax * w).ceil().min(w)) as u32;
    let y_min = (((a.y.min(b.y) - band) / ay * h).floor().max(0.0)) as u32;
    let y_max = (((a.y.max(b.y) + band) / ay * h).ceil().min(h)) as u32;
    for y in y_min..y_max {
        for x in x_min..x_max {
            let pu = (x as f32 + 0.5) / w * ax;
            let pv = (y as f32 + 0.5) / h * ay;
            let d = distance_to_segment(pu, pv, a, b).0;
            let idx = (y as usize) * (width as usize) + x as usize;
            if d < best[idx] {
                best[idx] = d;
            }
        }
    }
}

/// Whether a normalised cell centre can lie inside the polygon at all: the
/// polygon is contained in its own vertex bounding box, so a point outside it
/// is outside the polygon.
fn in_polygon_bbox(pu: f32, pv: f32, points: &[Point]) -> bool {
    let (mut x0, mut x1, mut y0, mut y1) = (f32::MAX, f32::MIN, f32::MAX, f32::MIN);
    for p in points {
        x0 = x0.min(p.x);
        x1 = x1.max(p.x);
        y0 = y0.min(p.y);
        y1 = y1.max(p.y);
    }
    !(pu < x0 || pu > x1 || pv < y0 || pv > y1)
}

pub fn apply_brush(
    grid: &mut SimulationGrid,
    stamp: &Stamp,
    stroke: &BrushStroke,
    flow: StrokeFlow,
) {
    let n = grid.cell_count();
    let k = stroke.pigment.min(grid.pigment_count.saturating_sub(1));
    for i in 0..n {
        let cov = stamp.coverage[i] * grid.bleed_mask[i];
        if cov <= 0.0 {
            continue;
        }
        let water = stroke.water * cov * stroke_water_factor(grid.paper_height[i]);
        grid.pressure[i] += water;
        grid.pigments_in_water[k * n + i] += stroke.concentration * cov;
        if water > WET_THRESHOLD || grid.pressure[i] > WET_THRESHOLD {
            grid.wet[i] = 1.0;
        }
        inject_flow(grid, stamp, i, cov, flow);
    }
}

pub fn apply_water(
    grid: &mut SimulationGrid,
    stamp: &Stamp,
    stroke: &WaterStroke,
    flow: StrokeFlow,
) {
    let n = grid.cell_count();
    for i in 0..n {
        let cov = stamp.coverage[i] * grid.bleed_mask[i];
        if cov <= 0.0 {
            continue;
        }
        grid.pressure[i] += stroke.water * cov * stroke_water_factor(grid.paper_height[i]);
        if grid.pressure[i] > WET_THRESHOLD {
            grid.wet[i] = 1.0;
        }
        inject_flow(grid, stamp, i, cov, flow);
    }
}

pub fn apply_lift(grid: &mut SimulationGrid, stamp: &Stamp, stroke: &LiftStroke) {
    let n = grid.cell_count();
    for i in 0..n {
        let f = (1.0 - stroke.strength * stamp.coverage[i]).clamp(0.0, 1.0);
        if f >= 1.0 {
            continue;
        }
        grid.pressure[i] *= f;
        for k in 0..grid.pigment_count {
            grid.pigments_in_water[k * n + i] *= f;
            grid.pigments_deposited[k * n + i] *= f;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn disc_stamp_is_full_at_centre_and_empty_far_away() {
        let flat = vec![0.5f32; 64 * 64];
        let s = rasterize_path(
            &[Point::new(0.5, 0.5)],
            RadiusProfile::uniform(0.2),
            0.5,
            StampTarget {
                width: 64,
                height: 64,
                paper_height: &flat,
            },
            Seed(1),
            StampParams::default(),
        );
        assert!(s.coverage[32 * 64 + 32] > 0.99);
        assert_eq!(s.coverage[0], 0.0);
    }

    /// Extent of the covered cells along each grid axis, in cells.
    fn extent(stamp: &Stamp) -> (usize, usize) {
        let w = stamp.width as usize;
        let mut cols = vec![false; w];
        let mut rows = vec![false; stamp.height as usize];
        for (i, &c) in stamp.coverage.iter().enumerate() {
            if c > 0.5 {
                cols[i % w] = true;
                rows[i / w] = true;
            }
        }
        (
            cols.iter().filter(|c| **c).count(),
            rows.iter().filter(|r| **r).count(),
        )
    }

    #[test]
    fn disc_on_a_four_to_one_scene_is_round_after_the_stretch() {
        let flat = vec![0.5f32; 128 * 128];
        let target = StampTarget {
            width: 128,
            height: 128,
            paper_height: &flat,
        };
        let params = StampParams {
            edge_roughness: 0.0,
            jitter: 0.0,
        };
        let disc = |aspect: f32| {
            rasterize_path_aspect(
                &[Point::new(0.5, 0.5)],
                RadiusProfile::uniform(0.1),
                0.0,
                target,
                aspect,
                Seed(1),
                params,
            )
        };
        let (cx, cy) = extent(&disc(4.0));
        // On the 4:1 output a grid cell is four times wider than it is tall,
        // so equal extents in scene units mean a 1:4 ratio in cells.
        let (ex, ey) = (cx as f32 * 4.0 / 128.0, cy as f32 / 128.0);
        assert!(
            (ex / ey - 1.0).abs() < 0.15,
            "extents in scene units {ex} vs {ey}"
        );
        assert!((ey - 0.2).abs() < 0.03, "diameter {ey}");
        let (sx, sy) = extent(&disc(1.0));
        assert_eq!(sx, sy);
        assert!(cx * 3 < sx);
    }

    #[test]
    fn mask_feather_is_isotropic_on_a_four_to_one_scene() {
        let m = Mask::Path {
            points: vec![Point::new(0.5, 0.5)],
            radius: 0.05,
            feather: 0.1,
        };
        let f = rasterize_mask_aspect(&m, 128, 128, 4.0);
        let centre = 64 * 128 + 64;
        let along_x = (0..64).take_while(|d| f[centre + d] > 0.0).count() as f32 * 4.0 / 128.0;
        let along_y = (0..64).take_while(|d| f[centre + d * 128] > 0.0).count() as f32 / 128.0;
        assert!(
            (along_x / along_y - 1.0).abs() < 0.2,
            "{along_x} vs {along_y}"
        );
    }

    #[test]
    fn polygon_mask_is_one_inside_and_feathers_outside() {
        let m = Mask::Polygon {
            points: vec![
                Point::new(0.25, 0.25),
                Point::new(0.75, 0.25),
                Point::new(0.75, 0.75),
                Point::new(0.25, 0.75),
            ],
            feather: 0.1,
        };
        let f = rasterize_mask(&m, 64, 64);
        assert_eq!(f[32 * 64 + 32], 1.0);
        let just_outside = f[32 * 64 + 50];
        assert!(just_outside > 0.0 && just_outside < 1.0);
        assert_eq!(f[0], 0.0);
    }

    const TILE_N: u32 = 128;
    static FLAT_PAPER: [f32; (TILE_N * TILE_N) as usize] = [0.5; (TILE_N * TILE_N) as usize];

    fn tile_target() -> StampTarget<'static> {
        StampTarget {
            width: TILE_N,
            height: TILE_N,
            paper_height: &FLAT_PAPER,
        }
    }

    fn multi_point_path() -> Vec<Point> {
        vec![
            Point::new(0.1, 0.3),
            Point::new(0.4, 0.68),
            Point::new(0.7, 0.34),
            Point::new(0.9, 0.6),
        ]
    }

    #[test]
    fn full_span_matches_the_unspanned_stamp() {
        let path = multi_point_path();
        let radius = RadiusProfile::uniform(0.05);
        let unspanned = rasterize_path_aspect(
            &path,
            radius,
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
        );
        let spanned = rasterize_path_span(
            &path,
            radius,
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
            StrokeSpan::FULL,
        );
        assert_eq!(unspanned.coverage, spanned.coverage);
    }

    #[test]
    fn spans_tile_the_stroke_without_double_coverage() {
        let path = multi_point_path();
        let radius = RadiusProfile::uniform(0.05);
        let full = rasterize_path_aspect(
            &path,
            radius,
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
        );
        let mut sum = vec![0.0f32; full.coverage.len()];
        for i in 0..8 {
            let span = StrokeSpan::new(i as f32 / 8.0, (i + 1) as f32 / 8.0);
            let part = rasterize_path_span(
                &path,
                radius,
                0.4,
                tile_target(),
                1.0,
                Seed(7),
                StampParams::default(),
                span,
            );
            for (acc, &c) in sum.iter_mut().zip(&part.coverage) {
                *acc += c;
            }
        }
        for (i, (&s, &f)) in sum.iter().zip(&full.coverage).enumerate() {
            assert!((s - f).abs() < 1e-5, "cell {i}: sum {s} vs full {f}");
            assert!(s <= f + 1e-9, "cell {i}: sum {s} exceeds full {f}");
        }
    }

    #[test]
    fn an_early_span_covers_only_the_start_of_the_path() {
        let path = vec![Point::new(0.1, 0.5), Point::new(0.9, 0.5)];
        let s = rasterize_path_span(
            &path,
            RadiusProfile::uniform(0.05),
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
            StrokeSpan::new(0.0, 0.25),
        );
        let w = s.width as usize;
        let mut past_mid = 0.0;
        for (i, &c) in s.coverage.iter().enumerate() {
            if i % w >= (w / 2) {
                past_mid += c;
            }
        }
        assert_eq!(past_mid, 0.0, "nothing past the path's midpoint");
        let near_start = s.coverage[(64 * w + 12) as usize];
        assert!(near_start > 0.0, "coverage near the path start");
    }

    #[test]
    fn a_single_point_path_lands_in_the_first_span() {
        let path = vec![Point::new(0.5, 0.5)];
        let radius = RadiusProfile::uniform(0.05);
        let full = rasterize_path_aspect(
            &path,
            radius,
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
        );
        let first = rasterize_path_span(
            &path,
            radius,
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
            StrokeSpan::new(0.0, 0.25),
        );
        let later = rasterize_path_span(
            &path,
            radius,
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
            StrokeSpan::new(0.25, 0.5),
        );
        assert_eq!(first.coverage, full.coverage, "the dab lands once, in full");
        assert!(
            later.coverage.iter().all(|&c| c == 0.0),
            "later spans get nothing"
        );
    }

    #[test]
    fn an_empty_span_lays_nothing() {
        let empty = StrokeSpan::new(0.5, 0.5);
        assert!(empty.is_empty());
        let s = rasterize_path_span(
            &multi_point_path(),
            RadiusProfile::uniform(0.05),
            0.4,
            tile_target(),
            1.0,
            Seed(7),
            StampParams::default(),
            empty,
        );
        assert!(s.coverage.iter().all(|&c| c == 0.0));
    }

    /// The span walk before it skipped the shared prefix: two full
    /// `cov_upto` walks, subtracted.
    #[allow(clippy::too_many_arguments)]
    fn reference_upto(
        path: &[Point],
        radius: RadiusProfile,
        softness: f32,
        target: StampTarget<'_>,
        aspect: f32,
        seed: Seed,
        params: StampParams,
        t_end: f32,
    ) -> Vec<f32> {
        let StampTarget {
            width,
            height,
            paper_height,
        } = target;
        let n = (width as usize) * (height as usize);
        let mut coverage = vec![0.0f32; n];
        let t_end = t_end.clamp(0.0, 1.0);
        if path.is_empty() || t_end <= 0.0 {
            return coverage;
        }
        let (w, h) = (width as f32, height as f32);
        let (ax, ay) = isotropic_scale(aspect);
        let iso = |p: &Point| Point::new(p.x * ax, p.y * ay);
        let segments: Vec<(Point, Point)> = if path.len() == 1 {
            vec![(iso(&path[0]), iso(&path[0]))]
        } else {
            path.windows(2).map(|p| (iso(&p[0]), iso(&p[1]))).collect()
        };
        let total_len: f32 = segments
            .iter()
            .map(|(a, b)| ((b.x - a.x).powi(2) + (b.y - a.y).powi(2)).sqrt())
            .sum::<f32>()
            .max(1e-6);
        let inner = 1.0 - softness.clamp(0.0, 1.0);
        let r_max = radius.max().max(1e-4) * (1.0 + params.jitter + params.edge_roughness);
        // A cell's size in the isotropic metric, for the anti-aliasing ramp.
        let cell = (ax / w).max(ay / h);
        // Only a partial span clips its final segment; a full span walks every
        // segment with the unclipped formula, so `StrokeSpan::FULL` stays
        // bit-identical to a single stamp.
        let clip = t_end < 1.0;
        let mut walked = 0.0f32;
        for (si, (a, b)) in segments.iter().enumerate() {
            let seg_len = ((b.x - a.x).powi(2) + (b.y - a.y).powi(2)).sqrt();
            let t0 = walked / total_len;
            let t1 = (walked + seg_len) / total_len;
            walked += seg_len;
            if t0 >= t_end {
                continue;
            }
            // Bounding box back in grid cells: the isotropic radius spans
            // r/ax of the width and r/ay of the height.
            let x_min = (((a.x.min(b.x) - r_max) / ax * w).floor().max(0.0)) as u32;
            let x_max = (((a.x.max(b.x) + r_max) / ax * w).ceil().min(w)) as u32;
            let y_min = (((a.y.min(b.y) - r_max) / ay * h).floor().max(0.0)) as u32;
            let y_max = (((a.y.max(b.y) + r_max) / ay * h).ceil().min(h)) as u32;
            if !clip || t1 <= t_end {
                // The segment lies fully inside the span: the original walk.
                for y in y_min..y_max {
                    for x in x_min..x_max {
                        let pu = (x as f32 + 0.5) / w * ax;
                        let pv = (y as f32 + 0.5) / h * ay;
                        let (dist, along) = distance_to_segment(pu, pv, *a, *b);
                        let t = t0 + (t1 - t0) * along;
                        let jitter = 1.0
                            + params.jitter
                                * (hash2(seed.0, (t * 64.0) as i32, si as i32) * 2.0 - 1.0);
                        let r = (radius.at(t) * jitter).max(1e-5);
                        let idx = (y as usize) * (width as usize) + x as usize;
                        let grain = paper_height.get(idx).copied().unwrap_or(0.5) - 0.5;
                        let shifted = dist + grain * params.edge_roughness * r;
                        let aa = 0.75 * cell / r;
                        let cov = falloff(shifted / r, inner.min(1.0 - aa));
                        if cov > coverage[idx] {
                            coverage[idx] = cov;
                        }
                    }
                }
            } else {
                // The span ends inside this segment: walk only up to the clip
                // point, but keep each cell's full-path `t` so the clip cap and
                // the unclipped walk share one radius and jitter.
                let frac = (t_end - t0) / (t1 - t0).max(1e-12);
                let c = Point::new(a.x + (b.x - a.x) * frac, a.y + (b.y - a.y) * frac);
                let x_min = (((a.x.min(c.x) - r_max) / ax * w).floor().max(0.0)) as u32;
                let x_max = (((a.x.max(c.x) + r_max) / ax * w).ceil().min(w)) as u32;
                let y_min = (((a.y.min(c.y) - r_max) / ay * h).floor().max(0.0)) as u32;
                let y_max = (((a.y.max(c.y) + r_max) / ay * h).ceil().min(h)) as u32;
                for y in y_min..y_max {
                    for x in x_min..x_max {
                        let pu = (x as f32 + 0.5) / w * ax;
                        let pv = (y as f32 + 0.5) / h * ay;
                        let (dist, along) = distance_to_segment(pu, pv, *a, *b);
                        let t = t0 + (t1 - t0) * along;
                        let jitter = 1.0
                            + params.jitter
                                * (hash2(seed.0, (t * 64.0) as i32, si as i32) * 2.0 - 1.0);
                        let r = (radius.at(t) * jitter).max(1e-5);
                        let cap_dist = ((pu - c.x).powi(2) + (pv - c.y).powi(2)).sqrt();
                        let dist = if t <= t_end { dist } else { cap_dist };
                        let idx = (y as usize) * (width as usize) + x as usize;
                        let grain = paper_height.get(idx).copied().unwrap_or(0.5) - 0.5;
                        let shifted = dist + grain * params.edge_roughness * r;
                        let aa = 0.75 * cell / r;
                        let cov = falloff(shifted / r, inner.min(1.0 - aa));
                        if cov > coverage[idx] {
                            coverage[idx] = cov;
                        }
                    }
                }
            }
        }
        coverage
    }

    /// Every span of every stroke equals the two full walks subtracted, to
    /// the bit, over paths with coincident points, axis-aligned, off-grid and
    /// one-point paths, tapered and grid-wide radii, non-square grids and
    /// rough paper.
    #[test]
    fn a_span_equals_its_two_full_walks_subtracted() {
        let mut state = 0x9E37_79B9_7F4A_7C15u64;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            (state >> 40) as f32 / (1u64 << 24) as f32
        };
        let mut checked = 0;
        for case in 0..240 {
            let (width, height) = [(64, 64), (37, 23), (50, 90), (96, 40)][case % 4];
            let paper: Vec<f32> = (0..width * height).map(|_| next()).collect();
            let target = StampTarget {
                width,
                height,
                paper_height: &paper,
            };
            let aspect = [1.0, 0.6, 1.7, 2.4][(case / 4) % 4];
            let points = 1 + (case % 7);
            let mut path: Vec<Point> = (0..points)
                .map(|_| Point::new(next() * 1.4 - 0.2, next() * 1.4 - 0.2))
                .collect();
            if case % 5 == 0 && points > 2 {
                path[1] = path[0];
            }
            // Axis-aligned segments: a row's `t` is then constant or runs one way.
            for i in 1..path.len() {
                match case % 6 {
                    1 => path[i].x = path[i - 1].x,
                    3 => path[i].y = path[i - 1].y,
                    _ => {}
                }
            }
            let reach = if case % 3 == 0 { 0.4 } else { 0.12 };
            let radius = RadiusProfile {
                start: 0.01 + next() * reach,
                end: 0.01 + next() * reach,
            };
            let softness = next();
            let seed = Seed(case as u64 * 31 + 7);
            let params = StampParams::default();
            let steps = 1 + (case % 13) as u32;
            let mut spans: Vec<StrokeSpan> = (0..steps)
                .map(|i| StrokeSpan::new(i as f32 / steps as f32, (i + 1) as f32 / steps as f32))
                .collect();
            let (a, b) = (next(), next());
            spans.extend([
                StrokeSpan::new(a, b),
                StrokeSpan::new(a, a),
                StrokeSpan::new(a, 1.0),
            ]);
            for span in spans {
                let got = rasterize_path_span(
                    &path, radius, softness, target, aspect, seed, params, span,
                );
                let mut want = reference_upto(
                    &path, radius, softness, target, aspect, seed, params, span.end,
                );
                if span.start > 0.0 {
                    let prefix = reference_upto(
                        &path, radius, softness, target, aspect, seed, params, span.start,
                    );
                    for (c, &p) in want.iter_mut().zip(&prefix) {
                        *c = (*c - p).max(0.0);
                    }
                }
                let bits = |v: &[f32]| v.iter().map(|c| c.to_bits()).collect::<Vec<_>>();
                assert_eq!(
                    bits(&got.coverage),
                    bits(&want),
                    "case {case} span {span:?}"
                );
                checked += 1;
            }
        }
        assert!(checked > 2000);
    }
}
