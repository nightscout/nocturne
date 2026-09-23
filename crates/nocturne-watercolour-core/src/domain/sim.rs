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
//! - Water also runs down a free surface `p + pool_relief * h` between wet
//!   neighbours (`pass_pool`), carrying suspended pigment at the donor's
//!   concentration. The divergence relaxation leaves the velocity
//!   divergence-free, so the paper-slope force alone can circulate water but
//!   never converge it; this flux is what pools a wash in the tooth's valleys.
//!   While the sheet dries the surface is lowered at the exposed rim in
//!   proportion to the drying rate, so evaporation draws a replacement
//!   current that carries pigment outward (Deegan's coffee ring), faint in
//!   standing water.
//! - Stroke water is modulated by paper height at the stamp
//!   (`paint::STROKE_WATER_PAPER_GAIN`), so a wash starts with pools in the
//!   paper's low regions rather than as a flat slab.
//!
//! # Numerical limits
//!
//! - `DT = 1` per tick. Velocities are clamped to `max_velocity` cells per
//!   tick (`0.45`), so `|u| + |v| <= 0.9 < 1` and the upwind advection never
//!   removes more than a cell holds.
//! - Viscosity and diffusion coefficients are per-neighbour explicit
//!   Laplacian weights and must stay below `0.25` total (`viscosity <= 0.25`,
//!   `pigment_diffusion <= 1.0` since it is divided by four).
//! - Divergence relaxation is a fixed `jacobi_iterations` (`8`) count, not
//!   iterated to a tolerance, so both backends do identical work.
//! - Deposited pigment is clamped to `1.0` per pigment; suspended pigment to
//!   `MAX_SUSPENDED`; water depth to `MAX_WATER_DEPTH`.
//! - Every field is clamped after each pass; a NaN in the inputs is not
//!   tolerated, `Scene::validate` rejects it upstream.

use super::grid::SimulationGrid;
use super::ops::{MAX_SETTLE_SHARE, Mask, Operation};
use super::paint::{self, StampParams, StampTarget};
use super::palette::Palette;
use super::seed::Seed;

pub const DT: f32 = 1.0;
pub const MAX_WATER_DEPTH: f32 = 8.0;
pub const MAX_SUSPENDED: f32 = 8.0;
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
    /// Per-neighbour weight (over `0.25`) of the free-surface flux: water
    /// runs from a higher free surface `p + pool_relief * h` to a lower one
    /// and carries its suspended pigment at the donor's concentration (see
    /// [`pass_pool`]).
    /// Explicit diffusion in its own pass: must stay at or below `1`.
    pub pool_rate: f32,
    /// Paper height's weight in the free surface, in water-depth units per
    /// height unit: standing water settles `pool_relief * dh` deeper in a
    /// valley than on the peak beside it.
    pub pool_relief: f32,
    /// Depth by which the free surface is lowered at a fully exposed rim,
    /// per unit of the sheet's drying drive: the Deegan replacement
    /// current. It carries pigment to the rim while the sheet dries and is
    /// faint in standing water, whose evaporation is small.
    pub edge_flow: f32,
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
            capillary_absorb: 0.003,
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
            pool_rate: 0.25,
            pool_relief: 0.5,
            edge_flow: 100.0,
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

    pass_pool(
        grid,
        params,
        &scratch.blurred,
        &mut scratch.g,
        &mut scratch.p,
    );
    std::mem::swap(&mut grid.pigments_in_water, &mut scratch.g);
    std::mem::swap(&mut grid.pressure, &mut scratch.p);

    pass_transfer(grid, pigments, params);

    pass_capillary(grid, params, &mut scratch.s);
    std::mem::swap(&mut grid.saturation, &mut scratch.s);
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
            grid.pigments_deposited[idx] = d.min(1.0);
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

/// How hard the sheet is drying: the evaporation rate of a film
/// `DRAIN_DEPTH` deep. A sheet-wide scalar rather than the cell's own rate,
/// so the free surface stays monotone in depth and the flux stays a
/// diffusion; a depth-dependent rim term turns anti-diffusive once the
/// wet mask breaks up and grows a checkerboard.
#[inline]
fn drying_drive(grid: &SimulationGrid, params: &SimParams) -> f32 {
    params.evaporation * grid.dry_rate + grid.settle_share * DRAIN_DEPTH
}

/// The potential the free-surface flux runs down: the water surface over the
/// paper relief, lowered at the exposed rim in proportion to how hard the
/// sheet is drying, so evaporation draws a replacement current outward.
#[inline]
fn free_surface(grid: &SimulationGrid, params: &SimParams, blurred_wet: &[f32], c: usize) -> f32 {
    let exposure = 1.0 - blurred_wet[c];
    grid.pressure[c] + params.pool_relief * grid.paper_height[c]
        - params.edge_flow * drying_drive(grid, params) * exposure
}

/// Free-surface flux from wet cell `i` to wet neighbour `j`, bounded by a
/// quarter of the donor's film so no cell gives up more than it holds.
/// Antisymmetric bit for bit (`pool_flux(j, i) == -pool_flux(i, j)`), so the
/// gather conserves water without a scatter.
#[inline]
fn pool_flux(
    grid: &SimulationGrid,
    params: &SimParams,
    blurred_wet: &[f32],
    i: usize,
    j: usize,
) -> f32 {
    if j == i || grid.wet[j] == 0.0 {
        return 0.0;
    }
    let p = &grid.pressure;
    let w = params.pool_rate * 0.25 * ((p[i] + p[j]) * 0.5 / params.diffusion_depth).min(1.0);
    let eta_i = free_surface(grid, params, blurred_wet, i);
    let eta_j = free_surface(grid, params, blurred_wet, j);
    (w * (eta_i - eta_j)).clamp(-0.25 * p[j], 0.25 * p[i])
}

/// Moves water and suspended pigment: conservative upwind advection, depth-
/// weighted diffusion between wet neighbours, the flow-outward water removal
/// near the wet boundary (Curtis's edge-darkening rule).
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

/// Runs water down the free surface between wet neighbours (see
/// [`free_surface`]) and carries suspended pigment with it at the donor's
/// concentration: pooling in the tooth's valleys and the drying rim's
/// replacement current. The projected velocity is divergence-free, so this is
/// the only way water converges. Its own pass so the pigment it moves is
/// bounded by the donor's pigment alone and cannot overdraw a cell that
/// advection and diffusion have already emptied.
pub fn pass_pool(
    grid: &SimulationGrid,
    params: &SimParams,
    blurred_wet: &[f32],
    out_g: &mut [f32],
    out_p: &mut [f32],
) {
    let n = grid.cell_count();
    let k_count = grid.pigment_count;
    let p = &grid.pressure;
    for i in 0..n {
        let nb = neighbours(grid, i);
        let flux = if grid.wet[i] == 0.0 {
            [0.0; 4]
        } else {
            nb.map(|j| pool_flux(grid, params, blurred_wet, i, j))
        };
        out_p[i] = (p[i] - flux.iter().sum::<f32>()).clamp(0.0, MAX_WATER_DEPTH);
        for k in 0..k_count {
            let g = &grid.pigments_in_water[k * n..(k + 1) * n];
            let mut ng = g[i];
            for (t, j) in nb.into_iter().enumerate() {
                let f = flux[t];
                if f > 0.0 {
                    ng -= f * g[i] / p[i];
                } else if f < 0.0 {
                    ng -= f * g[j] / p[j];
                }
            }
            out_g[k * n + i] = ng.clamp(0.0, MAX_SUSPENDED);
        }
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
        for (k, coef) in pigments.iter().enumerate().take(grid.pigment_count) {
            let idx = k * n + i;
            let g = grid.pigments_in_water[idx];
            let d = grid.pigments_deposited[idx];
            let mut down = g
                * (1.0 - h * coef.granulation)
                * coef.density
                * params.deposition_rate
                * settle_gate
                * DT;
            let mut up = d * (1.0 + (h - 1.0) * coef.granulation) * coef.density
                / coef.staining_power
                * params.lift_rate
                * DT;
            down = down.clamp(0.0, (1.0 - d).max(0.0));
            up = up.clamp(0.0, (MAX_SUSPENDED - g).max(0.0));
            grid.pigments_deposited[idx] = (d + down - up).clamp(0.0, 1.0);
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
                    (grid.pigments_deposited[idx] + grid.pigments_in_water[idx]).min(1.0);
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
