//! CPU reference simulation: a simplified Curtis et al. step. This module is
//! the specification the WGSL port mirrors pass for pass; each `pass_*`
//! function corresponds to one compute entry point and reads only the
//! previous pass's buffers, never its own output.
//!
//! # Deviations from Curtis
//!
//! - Velocities are collocated at cell centres rather than staggered.
//! - Water depth is advected with the same conservative upwind scheme as
//!   pigment, instead of being adjusted by the divergence relaxation. The
//!   pressure gradient then spreads water from deep to shallow cells, which
//!   is what makes a wet-into-wet drop bleed.
//! - Pigment diffuses between wet neighbours in proportion to water depth;
//!   Curtis relies on flow alone.
//! - Capillary transfer has no destination threshold (`delta`); it is bounded
//!   by the source saturation threshold and the destination's remaining
//!   capacity instead.
//! - Fibres under a wet cell give their water up at `wet_capillary_dry` share
//!   of the bare-paper rate, so the reservoir empties while a film still
//!   covers the cell; at full share the reservoir feeds the surface as fast as
//!   it evaporates and a drying sheet settles to equilibrium instead of
//!   drying.
//! - Capillary absorption and seep are transfers between the surface film and
//!   the paper fibres, not Curtis's one-way sinks: absorption takes the water
//!   it adds to the fibres out of the film, and a bloom takes the water it
//!   adds to a cell's film out of that cell's fibres. A sheet that
//!   manufactures water from an inexhaustible reservoir never dries.
//! - The flow-outward drain is scaled by local water depth
//!   (`clamp(p / DRAIN_DEPTH, DRAIN_MIN, DRAIN_MAX)`) and by paper height
//!   (`1.5 - h`): water that pooled in a low spot at the boundary drains, and
//!   so concentrates pigment, harder than a thin film on a high spot. The
//!   dried rim therefore varies in weight along the edge instead of reading
//!   as a uniform outline.
//! - Deposition is gated by film depth: a thinning film settles hard
//!   (`settle_base + dry_deposition * (1 - wet)^settle_curve`), while a deep
//!   film still settles by density (`wet_settle`) and stains by staining power
//!   (`stain_bite`), so a wash centre keeps colour instead of all of it riding
//!   the outward flow to the rim. Flow speed scales deposition down
//!   (`carry`), less for dense pigment, and lift scales with the wet fraction
//!   and flow speed (`lift_still`, `lift_flow_gain`): still or thin water
//!   barely re-suspends a deposit.
//! - Suspended pigment is also stirred by a drifting curl-noise current
//!   (`swirl`, [`pass_swirl`]): Curtis's grid resolves no convection inside a
//!   standing film, so without it a wet-into-wet wash only blurs. The current
//!   is exactly divergence free where the film is deep and away from its
//!   edge, and fades out toward both. Water itself is never swirled.
//! - Stroke water is modulated by paper height at the stamp
//!   (`paint::STROKE_WATER_PAPER_GAIN`), so a wash starts with pools in the
//!   paper's low regions rather than as a flat slab.
//!
//! # Numerical limits
//!
//! - `DT = 1` per tick. Velocities are clamped to `max_velocity` cells per
//!   tick (`0.45`), so `|u| + |v| <= 0.9 < 1` and the upwind advection never
//!   removes more than a cell holds. The swirl is a separate upwind pass
//!   whose face fluxes are clamped to `SWIRL_FACE_LIMIT`, so its four faces
//!   together never empty a cell either.
//! - Viscosity and diffusion coefficients are per-neighbour explicit
//!   Laplacian weights and must stay below `0.25` total (`viscosity <= 0.25`,
//!   `pigment_diffusion <= 1.0` since it is divided by four).
//! - Divergence relaxation is a fixed `jacobi_iterations` (`8`) count, not
//!   iterated to a tolerance, so both backends do identical work.
//! - Deposited pigment is clamped to `MAX_DEPOSITED` per pigment; suspended
//!   pigment to `MAX_SUSPENDED`; water depth to `MAX_WATER_DEPTH`.
//! - Every field is clamped after each pass; a NaN in the inputs is not
//!   tolerated, `Scene::validate` rejects it upstream.

use super::grid::SimulationGrid;
use super::ops::{MAX_SETTLE_SHARE, Mask, Operation};
use super::paint::{self, StampParams, StampTarget};
use super::palette::Palette;
use super::seed::Seed;
use super::swirl;

pub const DT: f32 = 1.0;
pub const MAX_WATER_DEPTH: f32 = 8.0;
pub const MAX_SUSPENDED: f32 = 8.0;
/// Deposited pigment per cell. Above `1.0` so a drying rim that concentrates
/// several cells' pigment keeps it instead of clipping; optics saturates
/// thickness, so the headroom reads as a darker rim, not black.
pub const MAX_DEPOSITED: f32 = MAX_SUSPENDED;
/// Floor on the flow carry factor: even fast water lets some pigment settle.
pub const CARRY_MIN: f32 = 0.1;
/// Density at which flow would stop carrying pigment; just past the heaviest
/// pigment (`1.0`), so moving water keeps every pigment partly suspended.
pub const CARRY_REACH: f32 = 1.2;
/// Water depth at which the flow-outward drain runs at its nominal rate.
pub const DRAIN_DEPTH: f32 = 0.5;
pub const DRAIN_MIN: f32 = 0.15;
pub const DRAIN_MAX: f32 = 2.0;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SimParams {
    pub slope_gain: f32,
    pub pressure_gain: f32,
    pub viscosity: f32,
    pub drag: f32,
    pub max_velocity: f32,
    pub jacobi_iterations: u32,
    /// Box-blur radius (cells) for the flow-outward mask.
    pub blur_radius: u32,
    /// Curtis eta: water removed per tick near the wet boundary.
    pub flow_outward_eta: f32,
    pub pigment_diffusion: f32,
    pub water_diffusion: f32,
    /// Water depth at which diffusion reaches full strength.
    pub diffusion_depth: f32,
    pub deposition_rate: f32,
    pub lift_rate: f32,
    /// Film depth at or below which a cell counts as drying. Just above
    /// `dry_threshold`.
    pub wet_lo: f32,
    /// Film depth at or above which the film counts as deep.
    pub wet_hi: f32,
    /// Deposition multiplier while the film is deep — pigment rides the water.
    pub settle_base: f32,
    /// Extra deposition as the film thins, over `settle_base`.
    pub dry_deposition: f32,
    /// Exponent on dryness.
    pub settle_curve: f32,
    /// Water the fibres draw from the film each tick, scaled by
    /// `Dry { rate }`: it is the sheet's largest sink, five times base
    /// evaporation, so leaving it unscaled would leave the settle rate no
    /// authority over how long the paper stays damp.
    pub capillary_absorb: f32,
    /// Fraction of capacity a cell must hold before it feeds neighbours.
    pub capillary_epsilon: f32,
    /// Fraction of capacity at which a dry cell becomes wet (a bloom).
    pub capillary_sigma: f32,
    pub capillary_rate: f32,
    pub capillary_dry: f32,
    /// Share of `capillary_dry` that fibres under a wet cell give up. A film
    /// on the surface slows the fibres drying but does not stop it; at zero
    /// the reservoir feeds the surface as fast as it evaporates and the sheet
    /// never finishes drying.
    pub wet_capillary_dry: f32,
    /// Water depth a bloom-wetted cell receives.
    pub capillary_seep: f32,
    pub evaporation: f32,
    pub dry_threshold: f32,
    /// Extra evaporation multiplier where the bleed mask is `0`.
    pub mask_evaporation: f32,
    pub stamp: StampParams,
    pub flow: paint::FlowParams,
    /// Settling in standing water, scaled by density: heavy pigment keeps
    /// dropping out of a deep film instead of riding it all to the rim.
    pub wet_settle: f32,
    /// Staining bite while wet, scaled by staining power and independent of
    /// density: a dye-like pigment adsorbs onto the fibres, so a wash centre
    /// keeps its colour.
    pub stain_bite: f32,
    /// How strongly flow speed keeps pigment suspended, reduced for dense
    /// pigment (`CARRY_REACH - density`).
    pub carry: f32,
    /// Share of lift a still film exerts; the rest needs moving water.
    pub lift_still: f32,
    /// Flow speed (cells/tick) gain at which lift reaches full strength.
    pub lift_flow_gain: f32,
    /// Peak swirl speed, short-axis cells per tick: slow enough that pigment
    /// drifts about one feature while a film stands, so tendrils form but a
    /// silhouette does not smear.
    pub swirl_speed: f32,
    /// Swirl noise lattice cells per isotropic unit; sets the tendril size
    /// relative to the artwork rather than to the grid.
    pub swirl_frequency: f32,
    /// Noise drift, lattice cells per tick, so the currents wander instead of
    /// circling fixed eddies.
    pub swirl_drift: f32,
    /// Film depth at which the swirl reaches full strength, fading to nothing
    /// at `wet_lo`: a thin film has no room to convect.
    pub swirl_depth: f32,
}

impl Default for SimParams {
    fn default() -> Self {
        SimParams {
            slope_gain: 0.6,
            pressure_gain: 0.2,
            viscosity: 0.1,
            drag: 0.02,
            max_velocity: 0.45,
            jacobi_iterations: 8,
            blur_radius: 5,
            flow_outward_eta: 0.06,
            pigment_diffusion: 0.6,
            water_diffusion: 0.1,
            diffusion_depth: 0.6,
            deposition_rate: 0.025,
            lift_rate: 0.002,
            wet_lo: 0.03,
            wet_hi: 0.5,
            settle_base: 0.02,
            dry_deposition: 6.0,
            settle_curve: 3.0,
            capillary_absorb: 0.006,
            capillary_epsilon: 0.45,
            capillary_sigma: 0.6,
            capillary_rate: 0.25,
            capillary_dry: 0.01,
            wet_capillary_dry: 0.35,
            capillary_seep: 0.12,
            evaporation: 0.001,
            dry_threshold: 0.02,
            mask_evaporation: 5.0,
            stamp: StampParams::default(),
            flow: paint::FlowParams::default(),
            wet_settle: 0.3,
            stain_bite: 0.6,
            carry: 2.0,
            lift_still: 0.25,
            lift_flow_gain: 3.0,
            swirl_speed: 0.16,
            swirl_frequency: 8.0,
            swirl_drift: 0.004,
            swirl_depth: 0.35,
        }
    }
}

/// Double-buffer storage for one step; sized for one grid.
#[derive(Debug, Clone)]
pub struct Scratch {
    u: Vec<f32>,
    v: Vec<f32>,
    div: Vec<f32>,
    q: Vec<f32>,
    q2: Vec<f32>,
    blur_tmp: Vec<f32>,
    blurred: Vec<f32>,
    g: Vec<f32>,
    p: Vec<f32>,
    s: Vec<f32>,
}

impl Scratch {
    pub fn for_grid(grid: &SimulationGrid) -> Scratch {
        let n = grid.cell_count();
        Scratch {
            u: vec![0.0; n],
            v: vec![0.0; n],
            div: vec![0.0; n],
            q: vec![0.0; n],
            q2: vec![0.0; n],
            blur_tmp: vec![0.0; n],
            blurred: vec![0.0; n],
            g: vec![0.0; n * grid.pigment_count],
            p: vec![0.0; n],
            s: vec![0.0; n],
        }
    }
}

/// Per-pigment behaviour coefficients in the layout the transfer pass reads.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PigmentCoefficients {
    pub density: f32,
    pub staining_power: f32,
    pub granulation: f32,
}

impl PigmentCoefficients {
    pub fn from_palette(palette: &Palette) -> Vec<PigmentCoefficients> {
        palette
            .pigments()
            .map(|p| PigmentCoefficients {
                density: p.density.clamp(0.0, 1.0),
                staining_power: p.staining_power.clamp(0.05, 1.0),
                granulation: p.granulation.clamp(0.0, 1.0),
            })
            .collect()
    }
}

/// One simulation tick.
pub fn step(
    grid: &mut SimulationGrid,
    pigments: &[PigmentCoefficients],
    params: &SimParams,
    scratch: &mut Scratch,
) {
    pass_velocity(grid, params, &mut scratch.u, &mut scratch.v);
    std::mem::swap(&mut grid.velocity_u, &mut scratch.u);
    std::mem::swap(&mut grid.velocity_v, &mut scratch.v);

    pass_divergence(grid, &mut scratch.div);
    scratch.q.iter_mut().for_each(|q| *q = 0.0);
    for _ in 0..params.jacobi_iterations {
        pass_jacobi(grid, &scratch.q, &scratch.div, &mut scratch.q2);
        std::mem::swap(&mut scratch.q, &mut scratch.q2);
    }
    pass_project(grid, params, &scratch.q, &mut scratch.u, &mut scratch.v);
    std::mem::swap(&mut grid.velocity_u, &mut scratch.u);
    std::mem::swap(&mut grid.velocity_v, &mut scratch.v);

    pass_blur_h(grid, params, &grid.wet, &mut scratch.blur_tmp);
    pass_blur_v(grid, params, &scratch.blur_tmp, &mut scratch.blurred);

    pass_advect(
        grid,
        params,
        &scratch.blurred,
        &mut scratch.g,
        &mut scratch.p,
    );
    std::mem::swap(&mut grid.pigments_in_water, &mut scratch.g);
    std::mem::swap(&mut grid.pressure, &mut scratch.p);

    pass_swirl(grid, params, &scratch.blurred, &mut scratch.g);
    std::mem::swap(&mut grid.pigments_in_water, &mut scratch.g);

    pass_transfer(grid, pigments, params);

    pass_capillary(grid, params, &mut scratch.s);
    std::mem::swap(&mut grid.saturation, &mut scratch.s);

    grid.tick = grid.tick.saturating_add(1);
}

/// Applies an operation. `seed` drives the stroke's radius jitter and should
/// derive from the scene seed and event index so replay is deterministic.
pub fn apply(grid: &mut SimulationGrid, op: &Operation, params: &SimParams, seed: Seed) {
    let (w, h) = (grid.width, grid.height);
    match op {
        Operation::Brush(s) => {
            let stamp = paint::rasterize_path_span(
                &s.path,
                s.radius,
                s.softness,
                StampTarget {
                    width: w,
                    height: h,
                    paper_height: &grid.paper_height,
                },
                grid.aspect,
                seed,
                params.stamp,
                s.span,
            );
            let flow = paint::stroke_flow(&s.path, s.span, grid.aspect, s.water, params.flow);
            paint::apply_brush(grid, &stamp, s, flow);
        }
        Operation::Water(s) => {
            let stamp = paint::rasterize_path_span(
                &s.path,
                s.radius,
                s.softness,
                StampTarget {
                    width: w,
                    height: h,
                    paper_height: &grid.paper_height,
                },
                grid.aspect,
                seed,
                params.stamp,
                s.span,
            );
            let flow = paint::stroke_flow(&s.path, s.span, grid.aspect, s.water, params.flow);
            paint::apply_water(grid, &stamp, s, flow);
        }
        Operation::Lift(s) => {
            let stamp = paint::rasterize_path_span(
                &s.path,
                s.radius,
                s.softness,
                StampTarget {
                    width: w,
                    height: h,
                    paper_height: &grid.paper_height,
                },
                grid.aspect,
                seed,
                params.stamp,
                s.span,
            );
            paint::apply_lift(grid, &stamp, s);
        }
        Operation::Dry { rate } => grid.dry_rate = rate.clamp(0.0, 64.0),
        Operation::Settle { share } => grid.settle_share = share.clamp(0.0, MAX_SETTLE_SHARE),
        Operation::DryAll => dry_all(grid),
        Operation::SetMask(mask) => set_mask(grid, mask),
        Operation::ClearMask => grid.bleed_mask.iter_mut().for_each(|m| *m = 1.0),
    }
}

pub fn set_mask(grid: &mut SimulationGrid, mask: &Mask) {
    grid.bleed_mask = paint::rasterize_mask_aspect(mask, grid.width, grid.height, grid.aspect);
}

/// Settles every suspended pigment where it stands and removes all water.
pub fn dry_all(grid: &mut SimulationGrid) {
    let n = grid.cell_count();
    for i in 0..n {
        for k in 0..grid.pigment_count {
            let idx = k * n + i;
            let d = grid.pigments_deposited[idx] + grid.pigments_in_water[idx];
            grid.pigments_deposited[idx] = d.min(MAX_DEPOSITED);
            grid.pigments_in_water[idx] = 0.0;
        }
        grid.pressure[i] = 0.0;
        grid.wet[i] = 0.0;
        grid.velocity_u[i] = 0.0;
        grid.velocity_v[i] = 0.0;
        grid.saturation[i] = 0.0;
    }
}

#[inline]
fn neighbours(grid: &SimulationGrid, i: usize) -> [usize; 4] {
    let w = grid.width as usize;
    let h = grid.height as usize;
    let x = i % w;
    let y = i / w;
    let l = if x > 0 { i - 1 } else { i };
    let r = if x + 1 < w { i + 1 } else { i };
    let u = if y > 0 { i - w } else { i };
    let d = if y + 1 < h { i + w } else { i };
    [l, r, u, d]
}

/// Bounds the velocity and zeroes any component that would carry water into
/// a dry neighbour, which is how the wet mask stays a hard boundary.
#[inline]
fn bound_velocity(
    grid: &SimulationGrid,
    params: &SimParams,
    i: usize,
    u: f32,
    v: f32,
) -> (f32, f32) {
    if grid.wet[i] == 0.0 {
        return (0.0, 0.0);
    }
    let [l, r, up, dn] = neighbours(grid, i);
    let mut u = u.clamp(-params.max_velocity, params.max_velocity);
    let mut v = v.clamp(-params.max_velocity, params.max_velocity);
    if u > 0.0 && (grid.wet[r] == 0.0 || r == i) {
        u = 0.0;
    }
    if u < 0.0 && (grid.wet[l] == 0.0 || l == i) {
        u = 0.0;
    }
    if v > 0.0 && (grid.wet[dn] == 0.0 || dn == i) {
        v = 0.0;
    }
    if v < 0.0 && (grid.wet[up] == 0.0 || up == i) {
        v = 0.0;
    }
    (u, v)
}

pub fn pass_velocity(
    grid: &SimulationGrid,
    params: &SimParams,
    out_u: &mut [f32],
    out_v: &mut [f32],
) {
    for i in 0..grid.cell_count() {
        let [l, r, up, dn] = neighbours(grid, i);
        let h = &grid.paper_height;
        let p = &grid.pressure;
        let gx =
            (h[r] - h[l]) * 0.5 * params.slope_gain + (p[r] - p[l]) * 0.5 * params.pressure_gain;
        let gy = (h[dn] - h[up]) * 0.5 * params.slope_gain
            + (p[dn] - p[up]) * 0.5 * params.pressure_gain;
        let u = grid.velocity_u[i];
        let v = grid.velocity_v[i];
        let lap_u =
            grid.velocity_u[l] + grid.velocity_u[r] + grid.velocity_u[up] + grid.velocity_u[dn]
                - 4.0 * u;
        let lap_v =
            grid.velocity_v[l] + grid.velocity_v[r] + grid.velocity_v[up] + grid.velocity_v[dn]
                - 4.0 * v;
        let nu = (u - DT * gx + params.viscosity * lap_u) * (1.0 - params.drag);
        let nv = (v - DT * gy + params.viscosity * lap_v) * (1.0 - params.drag);
        let (nu, nv) = bound_velocity(grid, params, i, nu, nv);
        out_u[i] = nu;
        out_v[i] = nv;
    }
}

pub fn pass_divergence(grid: &SimulationGrid, out: &mut [f32]) {
    for (i, o) in out.iter_mut().enumerate().take(grid.cell_count()) {
        let [l, r, up, dn] = neighbours(grid, i);
        *o = if grid.wet[i] > 0.0 {
            0.5 * (grid.velocity_u[r] - grid.velocity_u[l] + grid.velocity_v[dn]
                - grid.velocity_v[up])
        } else {
            0.0
        };
    }
}

pub fn pass_jacobi(grid: &SimulationGrid, q: &[f32], div: &[f32], out: &mut [f32]) {
    for i in 0..grid.cell_count() {
        if grid.wet[i] == 0.0 {
            out[i] = 0.0;
            continue;
        }
        let [l, r, up, dn] = neighbours(grid, i);
        out[i] = 0.25 * (q[l] + q[r] + q[up] + q[dn] - div[i]);
    }
}

pub fn pass_project(
    grid: &SimulationGrid,
    params: &SimParams,
    q: &[f32],
    out_u: &mut [f32],
    out_v: &mut [f32],
) {
    for i in 0..grid.cell_count() {
        let [l, r, up, dn] = neighbours(grid, i);
        let u = grid.velocity_u[i] - 0.5 * (q[r] - q[l]);
        let v = grid.velocity_v[i] - 0.5 * (q[dn] - q[up]);
        let (u, v) = bound_velocity(grid, params, i, u, v);
        out_u[i] = u;
        out_v[i] = v;
    }
}

pub fn pass_blur_h(grid: &SimulationGrid, params: &SimParams, src: &[f32], out: &mut [f32]) {
    let w = grid.width as i64;
    let radius = params.blur_radius as i64;
    let inv = 1.0 / (2 * radius + 1) as f32;
    for (i, o) in out.iter_mut().enumerate().take(grid.cell_count()) {
        let x = (i as i64) % w;
        let row = i as i64 - x;
        let mut sum = 0.0;
        for dx in -radius..=radius {
            let sx = (x + dx).clamp(0, w - 1);
            sum += src[(row + sx) as usize];
        }
        *o = sum * inv;
    }
}

pub fn pass_blur_v(grid: &SimulationGrid, params: &SimParams, src: &[f32], out: &mut [f32]) {
    let w = grid.width as i64;
    let h = grid.height as i64;
    let radius = params.blur_radius as i64;
    let inv = 1.0 / (2 * radius + 1) as f32;
    for (i, o) in out.iter_mut().enumerate().take(grid.cell_count()) {
        let x = (i as i64) % w;
        let y = (i as i64) / w;
        let mut sum = 0.0;
        for dy in -radius..=radius {
            let sy = (y + dy).clamp(0, h - 1);
            sum += src[(sy * w + x) as usize];
        }
        *o = sum * inv;
    }
}

/// Swirl gate of cell `j`: `0` where dry, rising to `1` as the film deepens
/// to `swirl_depth` and as the cell's `blur_radius` window becomes wholly
/// wet. A face into a dry cell carries nothing, so a gate that stepped
/// straight from `1` to `0` there would leave the edge cell's other faces
/// unbalanced and drain or flood it in a few ticks; the taper spreads that
/// imbalance over the window. `blurred_wet` treats the grid border as wet, so
/// the border's own share is taken from the distance to it.
#[inline]
fn swirl_gate(grid: &SimulationGrid, params: &SimParams, blurred_wet: &[f32], j: usize) -> f32 {
    if grid.wet[j] == 0.0 {
        return 0.0;
    }
    let w = grid.width as usize;
    let h = grid.height as usize;
    let (x, y) = (j % w, j / w);
    let border = x.min(y).min(w - 1 - x).min(h - 1 - y) as f32;
    let radius = params.blur_radius as f32;
    let border_wet = ((border + radius + 1.0) / (2.0 * radius + 1.0)).min(1.0);
    let interior = smoothstep(SWIRL_EDGE_WET, 1.0, blurred_wet[j].min(border_wet));
    interior * smoothstep(params.wet_lo, params.swirl_depth, grid.pressure[j])
}

/// Most of a cell the swirl may move through one face, so its four faces
/// together never move more than the cell holds.
pub const SWIRL_FACE_LIMIT: f32 = 0.25;
/// Wet share of the blur window at which the swirl starts: just under the
/// `~0.55` a cell on a straight wet edge sees, so edge cells stay still.
pub const SWIRL_EDGE_WET: f32 = 0.5;

/// Swirl flux of cell `i`'s four faces `(left, right, up, down)` from
/// `swirl::face_fluxes`, each scaled by the shallower side's gate. The gate
/// is symmetric in the two cells, so a face moves the same pigment out of one
/// as into the other; a face to a dry cell or the border carries nothing.
fn swirl_faces(
    grid: &SimulationGrid,
    params: &SimParams,
    blurred_wet: &[f32],
    i: usize,
) -> [f32; 4] {
    let gate_i = swirl_gate(grid, params, blurred_wet, i);
    if gate_i == 0.0 || params.swirl_speed <= 0.0 {
        return [0.0; 4];
    }
    let w = grid.width;
    let (x, y) = (i as u32 % w, i as u32 / w);
    let psi = |cx: u32, cy: u32| {
        swirl::stream(
            cx,
            cy,
            w,
            grid.aspect,
            grid.tick,
            grid.swirl_seed,
            params.swirl_speed,
            params.swirl_frequency,
            params.swirl_drift,
        )
    };
    let flux = swirl::face_fluxes(psi(x, y), psi(x + 1, y), psi(x, y + 1), psi(x + 1, y + 1));
    let mut out = [0.0; 4];
    for (f, j) in neighbours(grid, i).into_iter().enumerate() {
        if j != i {
            let gate = gate_i.min(swirl_gate(grid, params, blurred_wet, j));
            out[f] = (flux[f] * gate).clamp(-SWIRL_FACE_LIMIT, SWIRL_FACE_LIMIT);
        }
    }
    out
}

/// Stirs suspended pigment through the swirl's face fluxes ([`swirl_faces`])
/// with the same upwind rule as [`pass_advect`]. A separate pass so the
/// water's advection and diffusion keep their own stability budget; the face
/// limit bounds this one.
pub fn pass_swirl(
    grid: &SimulationGrid,
    params: &SimParams,
    blurred_wet: &[f32],
    out_g: &mut [f32],
) {
    let n = grid.cell_count();
    for i in 0..n {
        let [sl, sr, su, sd] = swirl_faces(grid, params, blurred_wet, i);
        let [l, r, up, dn] = neighbours(grid, i);
        let keep = 1.0 - (sr.max(0.0) + (-sl).max(0.0) + sd.max(0.0) + (-su).max(0.0)) * DT;
        let (in_l, in_r) = (sl.max(0.0) * DT, (-sr).max(0.0) * DT);
        let (in_u, in_d) = (su.max(0.0) * DT, (-sd).max(0.0) * DT);
        for k in 0..grid.pigment_count {
            let g = &grid.pigments_in_water[k * n..(k + 1) * n];
            let ng = g[i] * keep + in_l * g[l] + in_r * g[r] + in_u * g[up] + in_d * g[dn];
            out_g[k * n + i] = ng.clamp(0.0, MAX_SUSPENDED);
        }
    }
}

/// Moves water and suspended pigment: conservative upwind advection, depth-
/// weighted diffusion between wet neighbours, and the flow-outward water removal near
/// the wet boundary (Curtis's edge-darkening rule).
pub fn pass_advect(
    grid: &SimulationGrid,
    params: &SimParams,
    blurred_wet: &[f32],
    out_g: &mut [f32],
    out_p: &mut [f32],
) {
    let n = grid.cell_count();
    let k_count = grid.pigment_count;
    for i in 0..n {
        if grid.wet[i] == 0.0 {
            out_p[i] = grid.pressure[i];
            for k in 0..k_count {
                out_g[k * n + i] = grid.pigments_in_water[k * n + i];
            }
            continue;
        }
        let [l, r, up, dn] = neighbours(grid, i);
        let u = &grid.velocity_u;
        let v = &grid.velocity_v;
        let keep = 1.0 - (u[i].abs() + v[i].abs()) * DT;
        let in_l = if l != i { u[l].max(0.0) * DT } else { 0.0 };
        let in_r = if r != i { (-u[r]).max(0.0) * DT } else { 0.0 };
        let in_u = if up != i { v[up].max(0.0) * DT } else { 0.0 };
        let in_d = if dn != i { (-v[dn]).max(0.0) * DT } else { 0.0 };
        let p = &grid.pressure;
        let pi = p[i];
        let diffusion_weight = |j: usize, coef: f32| -> f32 {
            if j == i || grid.wet[j] == 0.0 {
                0.0
            } else {
                coef * 0.25 * ((pi + p[j]) * 0.5 / params.diffusion_depth).min(1.0)
            }
        };
        let wl = diffusion_weight(l, params.pigment_diffusion);
        let wr = diffusion_weight(r, params.pigment_diffusion);
        let wu = diffusion_weight(up, params.pigment_diffusion);
        let wd = diffusion_weight(dn, params.pigment_diffusion);
        for k in 0..k_count {
            let g = &grid.pigments_in_water[k * n..(k + 1) * n];
            let mut ng = g[i] * keep + in_l * g[l] + in_r * g[r] + in_u * g[up] + in_d * g[dn];
            ng +=
                wl * (g[l] - g[i]) + wr * (g[r] - g[i]) + wu * (g[up] - g[i]) + wd * (g[dn] - g[i]);
            out_g[k * n + i] = ng.clamp(0.0, MAX_SUSPENDED);
        }
        let wl = diffusion_weight(l, params.water_diffusion);
        let wr = diffusion_weight(r, params.water_diffusion);
        let wu = diffusion_weight(up, params.water_diffusion);
        let wd = diffusion_weight(dn, params.water_diffusion);
        let mut np = pi * keep + in_l * p[l] + in_r * p[r] + in_u * p[up] + in_d * p[dn];
        np += wl * (p[l] - pi) + wr * (p[r] - pi) + wu * (p[up] - pi) + wd * (p[dn] - pi);
        let drain = (pi / DRAIN_DEPTH).clamp(DRAIN_MIN, DRAIN_MAX) * (1.5 - grid.paper_height[i]);
        np -= params.flow_outward_eta * (1.0 - blurred_wet[i]) * drain * DT;
        out_p[i] = np.clamp(0.0, MAX_WATER_DEPTH);
    }
}

/// Hermite smoothstep of `x` between `lo` and `hi`, clamped to `0..1`.
#[inline]
fn smoothstep(lo: f32, hi: f32, x: f32) -> f32 {
    let t = ((x - lo) / (hi - lo).max(1e-6)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// Per-cell exchange between suspended and deposited pigment, evaporation,
/// capillary absorption and drying. In place: each cell reads only itself.
pub fn pass_transfer(
    grid: &mut SimulationGrid,
    pigments: &[PigmentCoefficients],
    params: &SimParams,
) {
    let n = grid.cell_count();
    for i in 0..n {
        if grid.wet[i] == 0.0 {
            continue;
        }
        let p = grid.pressure[i];
        let h = grid.paper_height[i];
        let m = grid.bleed_mask[i];
        let wet = smoothstep(params.wet_lo, params.wet_hi, p);
        let settle_gate =
            params.settle_base + params.dry_deposition * (1.0 - wet).powf(params.settle_curve);
        let speed = (grid.velocity_u[i] * grid.velocity_u[i]
            + grid.velocity_v[i] * grid.velocity_v[i])
            .sqrt();
        let lift_flow = wet
            * (params.lift_still
                + (1.0 - params.lift_still) * (speed * params.lift_flow_gain).min(1.0));
        for (k, coef) in pigments.iter().enumerate().take(grid.pigment_count) {
            let idx = k * n + i;
            let g = grid.pigments_in_water[idx];
            let d = grid.pigments_deposited[idx];
            let carry =
                (1.0 - speed * params.carry * (CARRY_REACH - coef.density)).clamp(CARRY_MIN, 1.0);
            let settle = coef.density * (settle_gate + params.wet_settle * wet)
                + params.stain_bite * coef.staining_power * wet;
            let mut down =
                g * (1.0 - h * coef.granulation) * settle * carry * params.deposition_rate * DT;
            let mut up = d * (1.0 + (h - 1.0) * coef.granulation) * coef.density
                / coef.staining_power
                * params.lift_rate
                * lift_flow
                * DT;
            down = down.clamp(0.0, (MAX_DEPOSITED - d).max(0.0));
            up = up.clamp(0.0, (MAX_SUSPENDED - g).max(0.0));
            grid.pigments_deposited[idx] = (d + down - up).clamp(0.0, MAX_DEPOSITED);
            grid.pigments_in_water[idx] = (g + up - down).clamp(0.0, MAX_SUSPENDED);
        }
        let boost = 1.0 + params.mask_evaporation * (1.0 - m);
        let evap = (params.evaporation * grid.dry_rate + grid.settle_share * p) * boost * DT;
        let mut np = (p - evap).max(0.0);
        let c = grid.capacity[i];
        let s = grid.saturation[i];
        let absorbed = (params.capillary_absorb * grid.dry_rate * DT)
            .min((c - s).max(0.0))
            .min(np);
        grid.saturation[i] = (s + absorbed).min(c);
        np -= absorbed;
        if np < params.dry_threshold {
            for k in 0..grid.pigment_count {
                let idx = k * n + i;
                grid.pigments_deposited[idx] =
                    (grid.pigments_deposited[idx] + grid.pigments_in_water[idx]).min(MAX_DEPOSITED);
                grid.pigments_in_water[idx] = 0.0;
            }
            np = 0.0;
            grid.wet[i] = 0.0;
            grid.velocity_u[i] = 0.0;
            grid.velocity_v[i] = 0.0;
        }
        grid.pressure[i] = np;
    }
}

#[inline]
fn capillary_transfer(params: &SimParams, s_from: f32, c_from: f32, s_to: f32, c_to: f32) -> f32 {
    if s_from > params.capillary_epsilon * c_from && s_from > s_to {
        ((s_from - s_to).min(c_to - s_to)).max(0.0) * 0.25 * params.capillary_rate * DT
    } else {
        0.0
    }
}

/// Spreads saturation through the paper fibres and wets cells that saturate
/// (blooms/backruns). Saturation is double-buffered; `wet` and `pressure` are
/// updated in place for the cell's own index only. Fibres under a wet cell
/// still give their water up, at `wet_capillary_dry` share of the bare-paper
/// rate, and a bloom takes the water it adds to the cell's film out of its
/// fibres, so re-wetting costs the reservoir and cannot cycle.
pub fn pass_capillary(grid: &mut SimulationGrid, params: &SimParams, out_s: &mut [f32]) {
    let n = grid.cell_count();
    for i in 0..n {
        let [l, r, up, dn] = neighbours(grid, i);
        let s = &grid.saturation;
        let c = &grid.capacity;
        let mut ns = s[i];
        for j in [l, r, up, dn] {
            if j == i {
                continue;
            }
            ns -= capillary_transfer(params, s[i], c[i], s[j], c[j]);
            ns += capillary_transfer(params, s[j], c[j], s[i], c[i]);
        }
        let share = if grid.wet[i] == 0.0 {
            1.0
        } else {
            params.wet_capillary_dry
        };
        ns *= 1.0 - params.capillary_dry * grid.dry_rate * DT * share;
        out_s[i] = ns.clamp(0.0, c[i].max(0.0));
    }
    for (i, slot) in out_s.iter_mut().enumerate() {
        let m = grid.bleed_mask[i];
        let ns = *slot;
        if grid.wet[i] == 0.0 && ns > params.capillary_sigma * grid.capacity[i] && m > 0.01 {
            let seep = (params.capillary_seep * m).min(ns);
            grid.wet[i] = 1.0;
            grid.pressure[i] = (grid.pressure[i] + seep).min(MAX_WATER_DEPTH);
            *slot -= seep;
        }
    }
}
