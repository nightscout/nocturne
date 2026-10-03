//! Sub-grid convection in standing water: the curl of a drifting value-noise
//! stream function. The curl of any scalar field is divergence free, so it
//! stirs suspended pigment into tendrils without piling it up.
//!
//! Everything per corner is integer hashing, `floor` and correctly rounded
//! `f32` addition and multiplication; every division is done once on the host
//! in [`Geometry`] and handed to the shader through the uniform. The WGSL
//! mirror in `flow.wgsl` therefore computes the same stream function up to
//! whatever multiply-add contraction the GPU driver applies. On either
//! backend it is a pure function of (corner, tick, seed), so a checkpoint
//! replay reproduces it exactly.

use super::scene::isotropic_scale;

/// Width of the taper that brings the stream function to zero at the edge
/// of the swirling region, as a share of one noise lattice cell. The edge
/// current along the taper scales with the stream function's drop across
/// it, so a taper much narrower than an eddy would run the edge several
/// times faster than the interior; at half a lattice cell the two are
/// comparable.
pub const SWIRL_TAPER: f32 = 0.5;

/// Drift along `y` per unit along `x`: an off-lattice direction, so the
/// field never repeats with the lattice period as it slides.
pub const SWIRL_DRIFT_SKEW: f32 = 0.618;

/// Most a face may carry per substep. Every cell's four fluxes cancel, so its
/// outflow is half their absolute sum, at most twice the largest face; at
/// `0.5` a substep can never move more than a cell holds.
pub const SWIRL_FACE_LIMIT: f32 = 0.5;

/// Largest face flux per unit of per-substep speed (`speed / substeps`). A
/// face flux is `T_a S_a - T_b S_b = T_a (S_a - S_b) + S_b (T_a - T_b)` over
/// its two corners, with taper `T <= 1` and centred noise `S`:
///
/// - `|S_a - S_b|` is at most the noise's steepest slope (`1.5` per lattice
///   cell) times one cell's lattice step, which works out to the speed over
///   the axis's isotropic factor (`>= 1`): at most `1.5`.
/// - `|S| <= 0.5 * scale`, and `T` is a smoothstep (slope `<= 1.5`) of a
///   distance that changes by at most `1 / (radius + 1)` per cell, where
///   `radius + 1` exceeds one taper width in cells: at most
///   `0.75 / SWIRL_TAPER`.
pub const SWIRL_FLUX_PER_SPEED: f32 = 1.5 + 0.75 / SWIRL_TAPER;

/// Cap on substeps per tick.
pub const SWIRL_MAX_SUBSTEPS: u32 = 64;

/// Largest `swirl_speed` the substep cap can keep within the face limit,
/// with a hair of headroom for `f32` rounding in the substep count; a faster
/// setting is clamped to it, so the flux bound always holds.
pub const SWIRL_MAX_SPEED: f32 =
    SWIRL_MAX_SUBSTEPS as f32 * SWIRL_FACE_LIMIT / SWIRL_FLUX_PER_SPEED * 0.999;

/// Per-scene swirl constants, computed once on the host from the grid size,
/// aspect and parameters and shared by both backends.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Geometry {
    /// Taper radii in cells along `x` and `y`: one [`SWIRL_TAPER`] width in
    /// the isotropic metric, at least one cell.
    pub radius_x: u32,
    pub radius_y: u32,
    /// `1 / (radius + 1)`: the normalised distance one cell step is worth.
    pub inv_x: f32,
    pub inv_y: f32,
    /// Noise lattice cells per grid cell along `x` and `y`.
    pub step_x: f32,
    pub step_y: f32,
    /// Stream-function scale per substep, in cells per tick per unit noise.
    pub scale: f32,
    /// Swirl passes per tick: enough that [`SWIRL_FLUX_PER_SPEED`] times the
    /// per-substep speed stays within [`SWIRL_FACE_LIMIT`].
    pub substeps: u32,
}

impl Geometry {
    /// `speed` is clamped to [`SWIRL_MAX_SPEED`].
    pub fn new(size: u32, aspect: f32, speed: f32, frequency: f32) -> Geometry {
        let (ax, ay) = isotropic_scale(aspect);
        let f = frequency.max(1e-3);
        let size_f = size as f32;
        let radius = |a: f32| ((size_f / a * SWIRL_TAPER / f) as u32).max(1);
        let (radius_x, radius_y) = (radius(ax), radius(ay));
        let speed = speed.clamp(0.0, SWIRL_MAX_SPEED);
        let substeps = ((speed * SWIRL_FLUX_PER_SPEED / SWIRL_FACE_LIMIT).ceil() as u32)
            .clamp(1, SWIRL_MAX_SUBSTEPS);
        Geometry {
            radius_x,
            radius_y,
            inv_x: 1.0 / (radius_x + 1) as f32,
            inv_y: 1.0 / (radius_y + 1) as f32,
            step_x: ax * f / size_f,
            step_y: ay * f / size_f,
            scale: speed / substeps as f32 * size_f / (f * ax * ay),
            substeps,
        }
    }
}

/// Lattice value in `[0, 1)` for integer point `(x, y)`; a lowbias32-style
/// integer mix, identical in WGSL `u32` arithmetic.
#[inline]
pub fn lattice(x: i32, y: i32, seed: u32) -> f32 {
    let mut h = (x as u32).wrapping_mul(0x8DA6_B343)
        ^ (y as u32).wrapping_mul(0xD816_3841)
        ^ seed.wrapping_mul(0xCB1A_B31F);
    h ^= h >> 16;
    h = h.wrapping_mul(0x7FEB_352D);
    h ^= h >> 15;
    h = h.wrapping_mul(0x846C_A68B);
    h ^= h >> 16;
    (h >> 8) as f32 * (1.0 / 16_777_216.0)
}

/// Smoothstep-interpolated value noise at `(qx, qy)`, in `[0, 1)`.
#[inline]
pub fn value_noise(qx: f32, qy: f32, seed: u32) -> f32 {
    let fx = qx.floor();
    let fy = qy.floor();
    let ix = fx as i32;
    let iy = fy as i32;
    let tx = qx - fx;
    let ty = qy - fy;
    let sx = tx * tx * (3.0 - 2.0 * tx);
    let sy = ty * ty * (3.0 - 2.0 * ty);
    let a = lattice(ix, iy, seed);
    let b = lattice(ix + 1, iy, seed);
    let c = lattice(ix, iy + 1, seed);
    let d = lattice(ix + 1, iy + 1, seed);
    a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy
}

/// Untapered, centred stream function at cell corner `(cx, cy)` (corner
/// `(x, y)` is the top-left of cell `(x, y)`). A face's flux is the
/// difference of its two corners.
#[inline]
pub fn stream(cx: u32, cy: u32, geo: &Geometry, tick: u32, seed: u32, drift: f32) -> f32 {
    let t = tick as f32 * drift;
    let n = value_noise(
        cx as f32 * geo.step_x + t,
        cy as f32 * geo.step_y + t * SWIRL_DRIFT_SKEW,
        seed,
    );
    geo.scale * (n - 0.5)
}

/// Face fluxes of one cell from its four corner stream values, top-left,
/// top-right, bottom-left, bottom-right: `(left, right, up, down)`, positive
/// along `+x` for the vertical faces and `+y` for the horizontal ones.
#[inline]
pub fn face_fluxes(tl: f32, tr: f32, bl: f32, br: f32) -> [f32; 4] {
    [bl - tl, br - tr, -(tr - tl), -(br - bl)]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn eddies_are_round_after_the_stretch() {
        // On a 4:1 grid an x cell spans four times the isotropic distance a
        // y cell does. Isotropic speed along x is the vertical-face flux (a
        // corner difference along y) times that span; along y, the
        // horizontal-face flux (a difference along x) times one.
        let geo = Geometry::new(400, 4.0, 1.0, 6.0);
        let psi = |x, y| stream(x, y, &geo, 0, 3, 0.0);
        let (mut along_x, mut along_y) = (0.0, 0.0);
        for y in 0..200 {
            for x in 0..200 {
                along_x += (psi(x, y + 1) - psi(x, y)).abs() * 4.0;
                along_y += (psi(x + 1, y) - psi(x, y)).abs();
            }
        }
        let ratio = along_x / along_y;
        assert!(
            (ratio - 1.0).abs() < 0.25,
            "isotropic x/y speed ratio {ratio}"
        );
    }

    #[test]
    fn a_zero_frequency_stays_finite() {
        let geo = Geometry::new(128, 1.0, 0.8, 0.0);
        assert!(stream(5, 9, &geo, 3, 1, 0.004).is_finite());
    }

    #[test]
    fn substeps_keep_the_flux_bound_within_the_face_limit() {
        // Aspects either side of square, and a 12-cell grid whose taper
        // radius rounds below one cell and is raised to it.
        for (size, aspect) in [(256, 1.0), (256, 0.25), (256, 4.0), (12, 1.0)] {
            for speed in [0.05, 0.8, 3.0, 10.0, 11.0, 1e6] {
                let geo = Geometry::new(size, aspect, speed, 8.0);
                let used = speed.min(SWIRL_MAX_SPEED);
                assert!(
                    used / geo.substeps as f32 * SWIRL_FLUX_PER_SPEED <= SWIRL_FACE_LIMIT,
                    "speed {speed} on {size} at {aspect} in {} substeps",
                    geo.substeps
                );
                assert!(geo.substeps <= SWIRL_MAX_SUBSTEPS);
            }
        }
        let tiny = Geometry::new(12, 1.0, 1.0, 8.0);
        assert_eq!((tiny.radius_x, tiny.radius_y), (1, 1), "max(1) binds");
    }
}
