//! Sub-grid convection in standing water: the curl of a drifting value-noise
//! stream function. The curl of any scalar field is divergence free, so it
//! stirs suspended pigment into tendrils without piling it up.
//!
//! Everything here is integer hashing and polynomial arithmetic in `f32`, a
//! pure function of (cell, tick, seed), so the WGSL mirror in `flow.wgsl`
//! computes the same field and a checkpoint replay reproduces it exactly.

use super::scene::isotropic_scale;

/// Width of the taper that brings the stream function to zero at the wet
/// edge, as a share of one noise lattice cell. The edge current along the
/// taper scales with the stream function's drop across it, so a taper much
/// narrower than an eddy would run the edge several times faster than the
/// interior; at half a lattice cell the two are comparable.
pub const SWIRL_TAPER: f32 = 0.5;

/// Drift along `y` per unit along `x`: an off-lattice direction, so the
/// field never repeats with the lattice period as it slides.
pub const SWIRL_DRIFT_SKEW: f32 = 0.618;

/// Box-blur radii `(x, y)`, in cells, of the gate field the taper reads: one
/// [`SWIRL_TAPER`] width in the isotropic metric on each axis, at least one
/// cell. Computed once on the host so both backends use the same integers.
pub fn taper_radii(size: u32, aspect: f32, frequency: f32) -> (u32, u32) {
    let (ax, ay) = isotropic_scale(aspect);
    let cells = |a: f32| ((size as f32 / a * SWIRL_TAPER / frequency.max(1e-3)) as u32).max(1);
    (cells(ax), cells(ay))
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

/// Centred stream function at cell corner `(cx, cy)` (corner `(x, y)` is the top-left
/// of cell `(x, y)`), scaled so a face's flux is the difference of its two
/// corners. `speed` is the flux, in short-axis cells per tick, for a unit
/// noise gradient; `frequency` is lattice cells per isotropic unit, so eddies
/// stay round after the square grid is stretched to `aspect`.
#[allow(clippy::too_many_arguments)]
#[inline]
pub fn stream(
    cx: u32,
    cy: u32,
    size: u32,
    aspect: f32,
    tick: u32,
    seed: u32,
    speed: f32,
    frequency: f32,
    drift: f32,
) -> f32 {
    let (ax, ay) = isotropic_scale(aspect);
    let px = cx as f32 / size as f32 * ax * frequency;
    let py = cy as f32 / size as f32 * ay * frequency;
    let t = tick as f32 * drift;
    let n = value_noise(px + t, py + t * SWIRL_DRIFT_SKEW, seed);
    let scale = speed * size as f32 / (frequency * ax * ay);
    scale * (n - 0.5)
}

/// Face fluxes of one cell from its four corner stream values, top-left,
/// top-right, bottom-left, bottom-right: `(left, right, up, down)`, positive
/// along `+x` for the vertical faces and `+y` for the horizontal ones. A face
/// shared by two cells reads the same two corners from both, and the four
/// fluxes of any cell sum to zero, so the field is exactly divergence free on
/// the grid, not only in the limit.
#[inline]
pub fn face_fluxes(tl: f32, tr: f32, bl: f32, br: f32) -> [f32; 4] {
    [bl - tl, br - tr, -(tr - tl), -(br - bl)]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_cell_is_exactly_balanced_and_faces_agree_between_neighbours() {
        let psi = |x, y| stream(x, y, 64, 2.0, 91, 5, 0.2, 6.0, 0.004);
        for y in 0..63 {
            for x in 0..63 {
                let [l, r, u, d] =
                    face_fluxes(psi(x, y), psi(x + 1, y), psi(x, y + 1), psi(x + 1, y + 1));
                assert!((r - l + d - u).abs() < 1e-6, "cell ({x},{y}) is a source");
                let [l_right, ..] = face_fluxes(
                    psi(x + 1, y),
                    psi(x + 2, y),
                    psi(x + 1, y + 1),
                    psi(x + 2, y + 1),
                );
                assert_eq!(
                    r, l_right,
                    "the shared face reads the same flux from both sides"
                );
            }
        }
    }

    #[test]
    fn eddies_are_round_after_the_stretch() {
        // On a 4:1 grid an x cell spans four times the isotropic distance a
        // y cell does. Isotropic speed along x is the vertical-face flux (a
        // corner difference along y) times that span; along y, the
        // horizontal-face flux (a difference along x) times one.
        let (size, aspect) = (400, 4.0);
        let psi = |x, y| stream(x, y, size, aspect, 0, 3, 1.0, 6.0, 0.0);
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
}
