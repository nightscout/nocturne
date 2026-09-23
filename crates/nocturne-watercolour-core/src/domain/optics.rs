//! Kubelka-Munk optics and the reference renderer.
//!
//! # Layer model
//!
//! Every cell is one mixed layer: the pigments present contribute
//! `Kx = sum_k t_k K_k` and `Sx = sum_k t_k S_k` per channel, where `t_k` is
//! the pigment's optical thickness (below). With
//! `a = (Kx + Sx) / Sx`, `b = sqrt(a^2 - 1)` and `beta = b Sx`,
//!
//! ```text
//! c = a sinh(beta) + b cosh(beta)
//! R = sinh(beta) / c
//! T = b / c
//! ```
//!
//! Layers stack with Curtis's two-layer formula, top layer 1 over layer 2:
//! `R = R1 + T1^2 R2 / (1 - R1 R2)`, `T = T1 T2 / (1 - R1 R2)`.
//!
//! # Optical thickness
//!
//! A cell's pigment amount is its deposit plus `wet_pigment_visibility` of its
//! suspended pigment, independent of the film's depth, so paint reads at
//! nearly full strength the moment it lands. The amount is not a KM thickness:
//! [`optical_thickness`] maps it through `max * g / (g + mid)`, `g =
//! amount^gamma`, which keeps halos faint, never reaches black, and gives the
//! headroom past the swatch that the simulation's deposit cap of 1 cannot.
//!
//! # Surface
//!
//! The gum film's surface reflections deepen heavy paint: the layer's
//! reflectance over its ground becomes Saunderson's
//! `R' = (1 - k1)(1 - k2) R / (1 - k2 R)`, blended in by coverage
//! ([`Surface`]). The transparent export applies it as the factor `R' / R` to
//! both the colour and the light the coverage layer returns, so the pixel
//! still composites to `R'` over white and `rgb <= alpha` holds.
//!
//! # Wet look
//!
//! At reconstructed depth `d`, `wet_look = clamp(d / sheen_depth, 0, 1)`. The
//! colour mix loses `wet_scatter_loss * wet_look` of its scattering and gains
//! `wet_absorb_gain * wet_look` absorption (water index-matches the
//! particles), the ground under it is scaled by `1 - wet_darken * wet_look`,
//! and the result gains `wet_sheen_add * wet_look`. The coverage mix is never
//! enriched, so alpha and the artwork's edge do not move as it dries; see
//! [`RenderParams`].
//!
//! # Transparent output
//!
//! Standard "over" compositing wants one alpha per pixel, but a glaze's colour
//! lives in its per-channel transmittance: over white, channel `c` shows
//! `W_c = R_c + T_c^2 / (1 - R_c)` (reflectance plus the light that goes down,
//! reflects off the paper and comes back up, interreflections included). The
//! layer is exported as
//!
//! ```text
//! return_c = T_c^2 / (1 - R_c)
//! alpha    = 1 - lerp(min_c(return_c), mean_c(return_c), ALPHA_SOFTNESS)
//! rgb_c    = max(0, W_c - (1 - alpha))            (premultiplied)
//! ```
//!
//! With `ALPHA_SOFTNESS = 0` this composites exactly to `W_c` over white for
//! every channel. The softness pulls alpha toward the mean transmittance so
//! a medium-thickness wash stays translucent over a dark ground instead of
//! saturating as soon as one channel is absorbed; the cost is that over
//! white the most-absorbed channel can come out brighter than `W_c` by at
//! most `ALPHA_SOFTNESS * (mean - min)` (a slight loss of saturation in
//! strongly chromatic glazes). Over black the result is
//! `R_c + return_c - (1 - alpha)`, brighter than the physical `R_c` in the
//! transmitting channels; over mid tones the interreflection term
//! `1 / (1 - R B)` is not background-aware. The errors are stated by
//! [`tests::alpha_conversion_matches_km_for_thin_glaze`]. This is an
//! approximation chosen so the output reads as watercolour on light grounds
//! in any compositor, not a claim of physical accuracy. Output is linear RGB;
//! sRGB encoding belongs to the exporter.
//!
//! # Composite modes
//!
//! The above is [`CompositeMode::Subtractive`]. Under it a pale glaze over a
//! dark ground cannot glow: a thin yellow returns almost no light of its own,
//! so a gold crescent on a night ground is a matte olive slab. For dark hosts
//! [`CompositeMode::Luminous`] is a *display* choice, not physics: it keeps
//! the same coverage alpha but outputs the colour the layer would show on
//! white paper,
//!
//! ```text
//! coverage  = 1 - lerp(min_c(return_c), mean_c(return_c), ALPHA_SOFTNESS)   of the
//!             plain mix (pigment thickness before granulation modulation)
//! alpha     = smoothstep(LUMINOUS_ALPHA_TOE, LUMINOUS_ALPHA_FULL, coverage of the
//!             plain mix's *presence*: its soft-dilated thickness around each cell)
//!           * smoothstep(LUMINOUS_EDGE_LO, LUMINOUS_EDGE_HI, mask)
//! W_c       = on-white colour of the damped granulated mix at thickness
//!             min(max(t_plain, t_presence, LUMINOUS_COLOUR_FLOOR),
//!                 LUMINOUS_COLOUR_CEILING), surface corrected
//! rgb_c     = W_c * alpha                                                  (premultiplied)
//! ```
//!
//! where `t_plain` is the plain mix's total thickness and the granulated mix
//! is first damped toward the plain one,
//! `plain + (textured - plain) * LUMINOUS_GRAIN_STRENGTH`. Fine-scale texture
//! therefore lives in colour, not alpha: granulation darkens and saturates
//! `W` as gentle mottling inside a continuous glow instead of thinning alpha
//! and letting the ground show through as speckle, while alpha still falls
//! off with the plain thickness at the boundary and in the halo. The paper's
//! low-frequency pooling octaves are part of the same height field the
//! granulation term reads, so they are damped by the same factor in colour;
//! the pooling the simulation deposited (the plain thickness itself) is not. Over a dark ground the
//! wash shows its on-white colour softly, like a translucent light wash, and
//! `rgb <= alpha` always holds. Two display decisions are folded in. The
//! alpha curve exists because the subtractive coverage is optical density,
//! which stays small for a pale pigment even at full thickness (moon gold:
//! 0.45 at thickness 1) and dips wherever the deposit carries paper tooth;
//! a display alpha has to read pigment presence and be flat across the
//! body, so coverage is mapped through a smoothstep that reaches 1 at
//! `LUMINOUS_ALPHA_FULL` (0.2) and is 0 below `LUMINOUS_ALPHA_TOE` (0.03),
//! with the smoothstep's gentle toe in between so halos and soft edges,
//! whose coverage falls through that band, still fade. The alpha reads the
//! plain thickness soft-dilated over simulation cells (`Sample::presence`):
//! a wash's deposit has genuine pinholes where the paper tooth left cells
//! almost bare, and per-pixel alpha would open each of them onto the ground
//! as a dark pit; presence fills a pit to most of its ring while grading a
//! boundary outward over about a cell.
//!
//! The second factor draws the outline. A dried deposit is bare or full cell
//! by cell, so a body's edge is a staircase, and the thickness term alone put
//! alpha's rise at the far tail of the reconstructed ramp: a dense body went
//! from nothing to opaque within a quarter of a cell, a one-pixel edge tracing
//! every step. `mask` (`Sample::mask`) is the cubic blend of how far inside
//! the paint each tap is. A cell is painted when it holds more than
//! `LUMINOUS_MASK_THICKNESS`; a tap takes the `PRESENCE_KERNEL`-weighted share
//! of its 3x3 that is painted, which rounds a staircase's corners toward the
//! line through them. A tap ringed by `MASK_MAJORITY` painted neighbours is
//! fully inside, which closes pinholes, and so is a painted tap with at most
//! `MASK_THIN` painted neighbours, so a dot or a one-cell line is kept whole
//! and only a body's corners round off. Its
//! 0.5 contour follows the body's boundary, and its ramp is set by the
//! geometry, not the thickness. Rims and overlaps inside a body do not move
//! it. The colour is taken
//! at no less than `LUMINOUS_COLOUR_FLOOR` thickness because a very thin
//! glaze's on-white colour is nearly white, and white times a small alpha
//! over black is grey. It is also raised to the pixel's presence, so a
//! boundary or a pinhole takes its neighbours' colour as it takes their
//! alpha, and capped at `LUMINOUS_COLOUR_CEILING`, past which an overlap
//! only darkens toward mud. In between the colour follows the deposited
//! thickness, so pooling and the deposit's fine tooth read as gentle
//! warm/pale mottling in colour, and overlaps darken further.
//! Over white it composites to `1 - alpha (1 - W_c)`: for a thin glaze
//! (`alpha` small) both modes are close to white and agree; for a dense
//! dark glaze on white Luminous is visibly washed out, and for a medium one
//! it is more saturated than the subtractive result because `W_c` is the
//! full-strength colour. That mismatch is why it is a per-scene choice
//! (`Background::TransparentOnDark`) and not the default. Pair it with the
//! normal palettes: `Palette::for_dark_surface` thins and pales pigments to
//! glow under Subtractive, which under Luminous only lowers alpha and drains
//! the on-white colour toward white.
//!
//! # Reconstruction
//!
//! The per-cell deposit and suspended-pigment fields are hard-edged once a
//! wash dries (each cell holds its own pigment), so bilinear reconstruction
//! over a sim grid coarser than the output shows the cell grid as two-pixel
//! staircases. The renderer instead reconstructs every field with a uniform
//! cubic B-spline: 4 by 4 taps around the sample position with weights
//!
//! ```text
//! w(-1) = (1 - t)^3 / 6
//! w(0)  = (3 t^3 - 6 t^2 + 4) / 6
//! w(+1) = (-3 t^3 + 3 t^2 + 3 t + 1) / 6
//! w(+2) = t^3 / 6
//! ```
//!
//! All weights are non-negative and sum to 1, so the filter is a convex
//! combination: reconstructed fields never go negative or overshoot, and the
//! premultiplied invariant (`rgb <= alpha`) holds exactly as before. At the
//! grid edge the out-of-bounds taps read the edge cell, which keeps the
//! weights summing to 1. The cost is four times the cell reads of the
//! previous bilinear: 16 taps instead of 4, each a separate storage read.
//! The Luminous alpha reads a *dilated* copy of the same field
//! (`Sample::presence`): at each of the 16 taps, `deposited + suspended *
//! visibility` is raised toward its 3x3 neighbourhood and the results
//! are blended with the same cubic weights. The dilation is the larger of
//! the tap cell's own value and the `PRESENCE_KERNEL`-weighted fourth-power
//! mean of the 3x3, i.e. a soft maximum:
//!
//! ```text
//! presence(tap) = max(tap, (sum_ij w_ij * x_ij^4)^(1/4)),  sum_ij w_ij = 1
//! ```
//!
//! A plain maximum fills a pit exactly but flattens every 3x3 to one value,
//! so the field it hands the alpha curve is a mosaic of plateaus whose
//! edges lie on cell boundaries - over a dark ground that reads as hard
//! grey blocks the size of a cell. The soft maximum keeps the pit-filling
//! (a bare cell ringed by `t` comes back at `0.93 t`) and the peaks (the
//! `max` with the cell's own value, so a stroke thinner than a cell is not
//! averaged away), but a cell beside a boundary now grades with its
//! neighbours' values instead of copying the largest, so the alpha edge
//! follows the deposit's sub-cell position rather than the lattice. Being
//! homogeneous and summing to 1, the kernel leaves a uniform field exactly
//! as it found it: the body of a wash is untouched. `render.wgsl` is the
//! lockstep port: both sides evaluate the same taps in the same order.

use super::grid::SimulationGrid;
use super::image::Image;
use super::palette::Palette;
use super::paper::PaperField;
use super::pigment::Rgb;

/// Upper bound on `beta`; `cosh` overflows `f32` near 89 and `T` is already
/// below `1e-17` here.
const MAX_BETA: f32 = 40.0;

/// See the module doc, "Transparent output". Mirrored in `render.wgsl`.
pub const ALPHA_SOFTNESS: f32 = 0.6;

/// Plain-mix coverage below which the luminous alpha is 0 and at which it
/// reaches 1; see the module doc, "Composite modes". Mirrored in
/// `render.wgsl`.
pub const LUMINOUS_ALPHA_TOE: f32 = 0.03;
pub const LUMINOUS_ALPHA_FULL: f32 = 0.2;

/// Reconstructed paint mask (`Sample::mask`) at which the luminous outline
/// starts and is fully in, either side of its 0.5 contour; see the module
/// doc, "Composite modes". Mirrored in `render.wgsl`.
pub const LUMINOUS_EDGE_LO: f32 = 0.2;
pub const LUMINOUS_EDGE_HI: f32 = 0.8;

/// Cell thickness above which a cell counts as painted for the luminous
/// outline. Mirrored in `render.wgsl`.
pub const LUMINOUS_MASK_THICKNESS: f32 = 0.02;

/// Hermite smoothstep of `x` between `edge0` and `edge1`.
pub fn smoothstep(edge0: f32, edge1: f32, x: f32) -> f32 {
    let t = ((x - edge0) / (edge1 - edge0).max(1e-6)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// Smallest plain thickness the luminous colour is evaluated at; see the
/// module doc, "Composite modes". Mirrored in `render.wgsl`.
pub const LUMINOUS_COLOUR_FLOOR: f32 = 0.5;

/// Largest optical thickness the luminous colour is evaluated at: past about
/// swatch depth an on-white colour only darkens toward mud, which over a
/// dark ground stops reading as light. Mirrored in `render.wgsl`.
pub const LUMINOUS_COLOUR_CEILING: f32 = 1.6;

/// How much of the granulation deviation reaches the luminous colour; `1`
/// is the full textured mix, `0` none. See the module doc, "Composite
/// modes". Mirrored in `render.wgsl`.
pub const LUMINOUS_GRAIN_STRENGTH: f32 = 0.38;

/// The Luminous display constants as one value, so probes can render
/// alternatives side by side. The shader only knows [`LUMINOUS_TUNING`].
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LuminousTuning {
    pub alpha_toe: f32,
    pub alpha_full: f32,
    pub colour_floor: f32,
    pub colour_ceiling: f32,
    pub grain_strength: f32,
}

/// The shipped tuning; mirrored constant for constant in `render.wgsl`.
pub const LUMINOUS_TUNING: LuminousTuning = LuminousTuning {
    alpha_toe: LUMINOUS_ALPHA_TOE,
    alpha_full: LUMINOUS_ALPHA_FULL,
    colour_floor: LUMINOUS_COLOUR_FLOOR,
    colour_ceiling: LUMINOUS_COLOUR_CEILING,
    grain_strength: LUMINOUS_GRAIN_STRENGTH,
};

/// The wet-look factors for one pixel: the film-depth factor and the two
/// strengths applied to it. At zero depth both terms vanish, so a dry frame
/// is bit-identical to one rendered with the wet look off.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WetLook {
    pub wet_look: f32,
    pub wet_darken: f32,
    pub wet_sheen_add: f32,
    pub wet_scatter_loss: f32,
    pub wet_absorb_gain: f32,
}

impl WetLook {
    pub const OFF: WetLook = WetLook {
        wet_look: 0.0,
        wet_darken: 0.0,
        wet_sheen_add: 0.0,
        wet_scatter_loss: 0.0,
        wet_absorb_gain: 0.0,
    };

    /// The colour mix as it reads under the film: water index-matches the
    /// particles, so less light scatters back and more is absorbed.
    fn enrich(&self, m: MixTotals) -> MixTotals {
        let f = self.wet_look;
        MixTotals {
            kx: m.kx.map(|k| k * (1.0 + self.wet_absorb_gain * f)),
            sx: m.sx.map(|s| s * (1.0 - self.wet_scatter_loss * f)),
            thickness: m.thickness,
        }
    }

    fn sheen(&self) -> f32 {
        self.wet_sheen_add * self.wet_look
    }

    fn ground(&self) -> f32 {
        1.0 - self.wet_darken * self.wet_look
    }
}

/// The Saunderson surface correction of the gum film; see the module doc,
/// "Surface". [`Surface::OFF`] leaves reflectance untouched.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Surface {
    /// External reflection at the air-film interface.
    pub k1: f32,
    /// Internal reflection at the film-air interface, seen from inside.
    pub k2: f32,
    /// Optical thickness times this is how much of the correction applies,
    /// capped at 1, so bare paper and faint halos keep their plain colour.
    pub coverage_gain: f32,
}

impl Surface {
    pub const OFF: Surface = Surface {
        k1: 0.0,
        k2: 0.0,
        coverage_gain: 0.0,
    };

    /// Per-channel factor `R' / R` for a film of optical thickness
    /// `thickness` whose uncorrected reflectance is `w`:
    /// `R' = (1 - k1)(1 - k2) R / (1 - k2 R)`, gated by coverage.
    pub fn factor(&self, w: Rgb, thickness: f32) -> Rgb {
        let gate = (thickness * self.coverage_gain).clamp(0.0, 1.0);
        let gain = (1.0 - self.k1) * (1.0 - self.k2);
        w.map(|w| {
            let q = gain / (1.0 - self.k2 * w.clamp(0.0, 1.0)).max(1e-6);
            1.0 + (q - 1.0) * gate
        })
    }
}

/// Deposited pigment amount to KM optical thickness: `max * g / (g + mid)`
/// with `g = x^gamma`. See the module doc, "Optical thickness".
pub fn optical_thickness(x: f32, gamma: f32, max: f32, mid: f32) -> f32 {
    if x <= 0.0 {
        return 0.0;
    }
    let g = x.powf(gamma);
    max * g / (g + mid)
}

/// How a layer's KM reflectance/transmittance becomes one premultiplied
/// pixel; see the module doc, "Composite modes".
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CompositeMode {
    /// Physically motivated: exact over white (up to `ALPHA_SOFTNESS`), dim
    /// over dark. For light hosts.
    #[default]
    Subtractive,
    /// Display choice for dark hosts: the on-white colour at coverage alpha.
    Luminous,
}

impl CompositeMode {
    /// Encoding written into the simulation state header so the render
    /// shader can read it; mirrored in `render.wgsl`.
    pub fn flag(self) -> f32 {
        match self {
            CompositeMode::Subtractive => 0.0,
            CompositeMode::Luminous => 1.0,
        }
    }

    pub fn from_flag(flag: f32) -> CompositeMode {
        if flag > 0.5 {
            CompositeMode::Luminous
        } else {
            CompositeMode::Subtractive
        }
    }
}

/// Reflectance and transmittance of one channel of a layer with total
/// absorption `kx` and scattering `sx` (coefficients already multiplied by
/// thickness). `(0, 0)` is a clear layer: `(R, T) = (0, 1)`.
pub fn layer(kx: f32, sx: f32) -> (f32, f32) {
    let kx = kx.max(0.0);
    let sx = sx.max(0.0);
    if kx + sx <= 0.0 {
        return (0.0, 1.0);
    }
    let beta = (kx * kx + 2.0 * kx * sx).sqrt().min(MAX_BETA);
    // a/b and Sx sinh(beta)/beta stay finite as Sx -> 0 where a and b alone blow up.
    let a_over_b = if beta > 1e-12 { (kx + sx) / beta } else { 1.0 };
    let sinh_over_beta = if beta < 1e-4 {
        1.0 + beta * beta / 6.0
    } else {
        beta.sinh() / beta
    };
    let denom = a_over_b * beta.sinh() + beta.cosh();
    let r = sx * sinh_over_beta / denom;
    let t = 1.0 / denom;
    (r.clamp(0.0, 1.0), t.clamp(0.0, 1.0))
}

pub fn layer_rgb(k: Rgb, s: Rgb, thickness: f32) -> (Rgb, Rgb) {
    let mut r = [0.0; 3];
    let mut t = [0.0; 3];
    for c in 0..3 {
        let (rc, tc) = layer(k.0[c] * thickness, s.0[c] * thickness);
        r[c] = rc;
        t[c] = tc;
    }
    (Rgb(r), Rgb(t))
}

/// Totals of a mixed layer holding `thickness[k]` of each palette pigment.
pub fn mixed_totals(palette: &Palette, thickness: &[f32]) -> MixTotals {
    let mut kx = [0.0f32; 3];
    let mut sx = [0.0f32; 3];
    let mut total = 0.0;
    for (p, &t) in palette.pigments().zip(thickness) {
        let t = t.max(0.0);
        total += t;
        for c in 0..3 {
            kx[c] += p.k.0[c] * t;
            sx[c] += p.s.0[c] * t;
        }
    }
    MixTotals {
        kx: Rgb(kx),
        sx: Rgb(sx),
        thickness: total,
    }
}

/// One mixed layer holding `thickness[k]` of each palette pigment.
pub fn mixed_layer(palette: &Palette, thickness: &[f32]) -> (Rgb, Rgb) {
    let m = mixed_totals(palette, thickness);
    layer_rgb(m.kx, m.sx, 1.0)
}

/// Stacks `layers` (index 0 on top) over an opaque background with
/// reflectance `background`, returning the reflectance seen from above.
pub fn composite(layers: &[(Rgb, Rgb)], background: Rgb) -> Rgb {
    let mut r_below = background;
    for (r1, t1) in layers.iter().rev() {
        r_below = Rgb([0, 1, 2].map(|c| {
            let denom = (1.0 - r1.0[c] * r_below.0[c]).max(1e-6);
            r1.0[c] + t1.0[c] * t1.0[c] * r_below.0[c] / denom
        }));
    }
    r_below
}

/// Light returned through a layer from a white ground, per channel.
fn returned(r: Rgb, t: Rgb) -> Rgb {
    t.zip(r, |t, r| (t * t / (1.0 - r).max(1e-6)).clamp(0.0, 1.0))
}

/// `1 - lerp(min, mean, ALPHA_SOFTNESS)` of the returned light: the
/// subtractive coverage alpha.
fn coverage(r: Rgb, t: Rgb) -> f32 {
    let ret = returned(r, t);
    let min_return = ret.0[0].min(ret.0[1]).min(ret.0[2]);
    let passed = min_return + (ret.mean() - min_return) * ALPHA_SOFTNESS;
    (1.0 - passed).clamp(0.0, 1.0)
}

/// On-white colour of a layer, `R + T^2 / (1 - R)`.
pub fn over_white(r: Rgb, t: Rgb) -> Rgb {
    r.zip(returned(r, t), |r, ret| (r + ret).clamp(0.0, 1.0))
}

/// Colour of a layer over a ground of reflectance `ground`; `ground = 1`
/// recovers [`over_white`]. The wet look darkens the ground under a film
/// this way rather than scaling pigment thickness, so it never fakes extra
/// pigment.
fn over_ground(r: Rgb, t: Rgb, ground: f32) -> Rgb {
    r.zip(t, |r, t| {
        (r + t * t * ground / (1.0 - r * ground).max(1e-6)).clamp(0.0, 1.0)
    })
}

/// Subtractive premultiplied RGBA for standard "over" compositing; see the
/// module doc.
pub fn to_premultiplied(r: Rgb, t: Rgb) -> [f32; 4] {
    let alpha = coverage(r, t);
    let passed = 1.0 - alpha;
    let w = over_white(r, t);
    [
        (w.0[0] - passed).clamp(0.0, 1.0),
        (w.0[1] - passed).clamp(0.0, 1.0),
        (w.0[2] - passed).clamp(0.0, 1.0),
        alpha,
    ]
}

/// Totals of a mixed layer: absorption and scattering with thickness
/// multiplied in, and the thickness sum.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MixTotals {
    pub kx: Rgb,
    pub sx: Rgb,
    pub thickness: f32,
}

/// `plain + (textured - plain) * strength`, component-wise.
pub fn damp_texture(textured: MixTotals, plain: MixTotals, strength: f32) -> MixTotals {
    MixTotals {
        kx: plain.kx.zip(textured.kx, |p, t| p + (t - p) * strength),
        sx: plain.sx.zip(textured.sx, |p, t| p + (t - p) * strength),
        thickness: plain.thickness + (textured.thickness - plain.thickness) * strength,
    }
}

/// Luminous premultiplied RGBA; see the module doc, "Composite modes".
/// `textured` is the mix after granulation modulation (colour), `plain` the
/// same mix before it (colour reference thickness), `presence` the plain
/// mix dilated over the cell neighbourhood (alpha).
pub fn to_premultiplied_luminous(
    textured: MixTotals,
    plain: MixTotals,
    presence: MixTotals,
) -> [f32; 4] {
    to_premultiplied_luminous_tuned(
        textured,
        plain,
        presence,
        1.0,
        &LUMINOUS_TUNING,
        WetLook::OFF,
        Surface::OFF,
    )
}

/// [`to_premultiplied_luminous`] with an explicit grain strength, for
/// side-by-side probes; the shader only knows the constant.
pub fn to_premultiplied_luminous_with_strength(
    textured: MixTotals,
    plain: MixTotals,
    presence: MixTotals,
    grain_strength: f32,
) -> [f32; 4] {
    let tuning = LuminousTuning {
        grain_strength,
        ..LUMINOUS_TUNING
    };
    to_premultiplied_luminous_tuned(
        textured,
        plain,
        presence,
        1.0,
        &tuning,
        WetLook::OFF,
        Surface::OFF,
    )
}

/// [`to_premultiplied_luminous`] with an explicit tuning, the reconstructed
/// paint `mask`, wet-look factors and surface correction. A `mask` of 1 is a
/// pixel well inside the paint, where alpha is the thickness term alone. The
/// wet look and the surface act on the on-white colour exactly as the
/// subtractive path's do; alpha is untouched by both.
pub fn to_premultiplied_luminous_tuned(
    textured: MixTotals,
    plain: MixTotals,
    presence: MixTotals,
    mask: f32,
    tuning: &LuminousTuning,
    wet: WetLook,
    surface: Surface,
) -> [f32; 4] {
    let (r, t) = layer_rgb(presence.kx, presence.sx, 1.0);
    let body = smoothstep(tuning.alpha_toe, tuning.alpha_full, coverage(r, t));
    let alpha = body * smoothstep(LUMINOUS_EDGE_LO, LUMINOUS_EDGE_HI, mask);
    // Brings the layer's thickness up to its presence (so a boundary and a
    // pinhole take their neighbours' colour, as they take their alpha) or the
    // colour floor, and down to the ceiling.
    let reference = plain.thickness.max(1e-6);
    let scale = (tuning.colour_floor.max(presence.thickness) / reference)
        .max(1.0)
        .min(tuning.colour_ceiling / reference);
    let damped = wet.enrich(damp_texture(textured, plain, tuning.grain_strength));
    let (r_ref, t_ref) = layer_rgb(damped.kx, damped.sx, scale);
    let w = over_ground(r_ref, t_ref, wet.ground());
    let q = surface.factor(w, plain.thickness * scale);
    let w = w.zip(q, |c, q| (c * q + wet.sheen()).clamp(0.0, 1.0));
    [w.0[0] * alpha, w.0[1] * alpha, w.0[2] * alpha, alpha]
}

/// Subtractive premultiplied RGBA whose colour comes from the mix `colour`
/// and whose coverage alpha comes from the mix `cover`; with equal mixes and
/// no wet look or surface this is [`to_premultiplied`]. The wet look enriches
/// the colour mix, darkens the ground it composites over and adds a sheen;
/// the surface correction scales both the colour and the light the coverage
/// mix returns, so over white the pixel still composites to the corrected
/// colour and `rgb <= alpha` holds.
fn to_premultiplied_with_coverage(
    colour: MixTotals,
    cover: MixTotals,
    wet: WetLook,
    surface: Surface,
) -> [f32; 4] {
    let (r_c, t_c) = layer_rgb(cover.kx, cover.sx, 1.0);
    let q_c = surface.factor(over_white(r_c, t_c), cover.thickness);
    let ret = returned(r_c, t_c).zip(q_c, |ret, q| ret * q);
    let min_return = ret.0[0].min(ret.0[1]).min(ret.0[2]);
    let passed = (min_return + (ret.mean() - min_return) * ALPHA_SOFTNESS).clamp(0.0, 1.0);
    let enriched = wet.enrich(colour);
    let (r, t) = layer_rgb(enriched.kx, enriched.sx, 1.0);
    let w = over_ground(r, t, wet.ground());
    let q = surface.factor(w, colour.thickness);
    let w = w.zip(q, |c, q| (c * q + wet.sheen()).clamp(0.0, 1.0));
    [
        (w.0[0] - passed).clamp(0.0, 1.0),
        (w.0[1] - passed).clamp(0.0, 1.0),
        (w.0[2] - passed).clamp(0.0, 1.0),
        1.0 - passed,
    ]
}

/// Dispatches on the mode with no wet look and no surface correction: the
/// bare KM conversion. Subtractive renders the colour mix as is; the alpha/coverage comes from `coverage` (identical to `textured` when
/// dry), so the film deepens colour without widening the wash. Luminous
/// splits texture into colour and dilated plain presence into alpha.
pub fn composite_pixel(
    textured: MixTotals,
    plain: MixTotals,
    presence: MixTotals,
    mode: CompositeMode,
) -> [f32; 4] {
    composite_pixel_with_strength(textured, plain, presence, mode, LUMINOUS_GRAIN_STRENGTH)
}

/// [`composite_pixel`] with an explicit luminous grain strength; Subtractive
/// ignores it and `presence`.
pub fn composite_pixel_with_strength(
    textured: MixTotals,
    plain: MixTotals,
    presence: MixTotals,
    mode: CompositeMode,
    grain_strength: f32,
) -> [f32; 4] {
    let tuning = LuminousTuning {
        grain_strength,
        ..LUMINOUS_TUNING
    };
    composite_pixel_tuned(
        textured,
        textured,
        plain,
        presence,
        1.0,
        mode,
        &tuning,
        WetLook::OFF,
        Surface::OFF,
    )
}

/// [`composite_pixel`] with an explicit coverage mix, wet-look factors and
/// surface correction. Subtractive derives its alpha from `coverage` instead
/// of `textured`; the wet look (enriched colour mix, darkened ground, sheen)
/// applies to both modes without touching alpha; the wrapper
/// [`composite_pixel`] passes `textured` for the coverage, a zero wet look
/// and [`Surface::OFF`], the bare KM conversion.
#[allow(clippy::too_many_arguments)]
pub fn composite_pixel_tuned(
    textured: MixTotals,
    coverage: MixTotals,
    plain: MixTotals,
    presence: MixTotals,
    mask: f32,
    mode: CompositeMode,
    tuning: &LuminousTuning,
    wet: WetLook,
    surface: Surface,
) -> [f32; 4] {
    match mode {
        CompositeMode::Subtractive => {
            to_premultiplied_with_coverage(textured, coverage, wet, surface)
        }
        CompositeMode::Luminous => {
            to_premultiplied_luminous_tuned(textured, plain, presence, mask, tuning, wet, surface)
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RenderParams {
    /// How strongly paper height at output resolution modulates thickness of
    /// granulating pigments.
    pub granulation_gain: f32,
    /// Fraction of suspended pigment that reads through the water film,
    /// whatever the film's depth: paint in water is visible the moment it
    /// lands, a little lighter than the same paint settled.
    pub wet_pigment_visibility: f32,
    /// Scales pigment amount before [`optical_thickness`].
    pub thickness_scale: f32,
    /// Fraction of the ground's reflectance the wet-look factor removes: a
    /// film darkens the paper under it. Mirrored in `render.wgsl`.
    pub wet_darken: f32,
    /// Reflectance added to the result while a film is present; the faint
    /// wet sheen. Mirrored in `render.wgsl`.
    pub wet_sheen_add: f32,
    /// Water depth at which the wet-look factor reaches 1.
    pub sheen_depth: f32,
    /// Fraction of the colour mix's scattering the film removes at full wet
    /// look: water index-matches the particles, so wet paint reads richer.
    pub wet_scatter_loss: f32,
    /// Fraction of absorption the film adds at full wet look.
    pub wet_absorb_gain: f32,
    /// [`optical_thickness`]'s exponent: above 1 opens up the pale range.
    pub optical_gamma: f32,
    /// [`optical_thickness`]'s ceiling: a wash approaches masstone, never black.
    pub optical_max: f32,
    /// [`optical_thickness`]'s half-saturation point in `x^gamma`.
    pub optical_mid: f32,
    /// See [`Surface`].
    pub surface_k1: f32,
    pub surface_k2: f32,
    pub surface_coverage_gain: f32,
}

impl RenderParams {
    /// The KM thickness a pigment amount `x` renders at.
    pub fn optical(&self, x: f32) -> f32 {
        optical_thickness(
            x * self.thickness_scale,
            self.optical_gamma,
            self.optical_max,
            self.optical_mid,
        )
    }

    pub fn surface(&self) -> Surface {
        Surface {
            k1: self.surface_k1,
            k2: self.surface_k2,
            coverage_gain: self.surface_coverage_gain,
        }
    }
}

impl Default for RenderParams {
    fn default() -> Self {
        RenderParams {
            granulation_gain: 0.8,
            wet_pigment_visibility: 0.85,
            thickness_scale: 1.0,
            wet_darken: 0.12,
            wet_sheen_add: 0.018,
            sheen_depth: 0.35,
            wet_scatter_loss: 0.5,
            wet_absorb_gain: 0.2,
            // A dried light wash (amount ~0.4) lands near its swatch
            // (optical ~0.85); a full deposit (amount 1) reads past it at ~1.5.
            optical_gamma: 1.35,
            optical_max: 2.2,
            optical_mid: 0.47,
            // A gum-arabic film: ~3 % external and ~40 % internal reflection.
            surface_k1: 0.03,
            surface_k2: 0.4,
            // Full correction from optical thickness 2/3 up.
            surface_coverage_gain: 1.5,
        }
    }
}

/// Renders the grid to `paper_out`'s resolution, reconstructing the sim
/// fields with the cubic B-spline filter described in the module doc and
/// sampling the paper height at full output resolution so granulation keeps
/// its texture regardless of sim resolution. The paper field is generated at
/// the output's pixel scale (see `paper::generate_with_pixel_scale`), which
/// band-limits octaves finer than a few output pixels so the granulation
/// never reads as per-pixel speckle. The composite mode is the grid's
/// (`SimulationGrid::composite_mode`), so every backend reads it from the
/// same state.
pub fn render(
    grid: &SimulationGrid,
    palette: &Palette,
    paper_out: &PaperField,
    params: &RenderParams,
) -> Image {
    render_with_grain_strength(grid, palette, paper_out, params, LUMINOUS_GRAIN_STRENGTH)
}

/// [`render`] with an explicit `LUMINOUS_GRAIN_STRENGTH` override, for probes
/// that compare strengths side by side. Subtractive output is unaffected.
pub fn render_with_grain_strength(
    grid: &SimulationGrid,
    palette: &Palette,
    paper_out: &PaperField,
    params: &RenderParams,
    grain_strength: f32,
) -> Image {
    let tuning = LuminousTuning {
        grain_strength,
        ..LUMINOUS_TUNING
    };
    render_with_luminous_tuning(grid, palette, paper_out, params, &tuning)
}

/// [`render`] with an explicit luminous tuning, for side-by-side probes.
/// Subtractive output is unaffected.
pub fn render_with_luminous_tuning(
    grid: &SimulationGrid,
    palette: &Palette,
    paper_out: &PaperField,
    params: &RenderParams,
    tuning: &LuminousTuning,
) -> Image {
    let (ow, oh) = (paper_out.width, paper_out.height_px);
    let mut image = Image::new(ow, oh);
    let k_count = grid.pigment_count.min(palette.len());
    let mut thickness = vec![0.0f32; k_count];
    let mut plain = vec![0.0f32; k_count];
    let mut presence = vec![0.0f32; k_count];
    let mut coverage = vec![0.0f32; k_count];
    let gran: Vec<f32> = palette.pigments().map(|p| p.granulation).collect();
    for y in 0..oh {
        for x in 0..ow {
            let u = (x as f32 + 0.5) / ow as f32;
            let v = (y as f32 + 0.5) / oh as f32;
            let sample = cubic_sample(grid, u, v, params.wet_pigment_visibility);
            let h_out = paper_out.height[(y as usize) * (ow as usize) + x as usize];
            let wet_look = (sample.depth / params.sheen_depth).clamp(0.0, 1.0);
            for k in 0..k_count {
                let base =
                    sample.deposited[k] + sample.suspended[k] * params.wet_pigment_visibility;
                let grain = 1.0 + gran[k] * params.granulation_gain * (0.5 - h_out) * 2.0;
                let plain_base = params.optical(base.max(0.0));
                let grained = (plain_base * grain.max(0.0)).max(0.0);
                coverage[k] = grained;
                plain[k] = plain_base;
                thickness[k] = grained;
                presence[k] = params.optical(sample.presence[k].max(0.0));
            }
            let px = composite_pixel_tuned(
                mixed_totals(palette, &thickness),
                mixed_totals(palette, &coverage),
                mixed_totals(palette, &plain),
                mixed_totals(palette, &presence),
                sample.mask,
                grid.composite_mode,
                tuning,
                WetLook {
                    wet_look,
                    wet_darken: params.wet_darken,
                    wet_sheen_add: params.wet_sheen_add,
                    wet_scatter_loss: params.wet_scatter_loss,
                    wet_absorb_gain: params.wet_absorb_gain,
                },
                params.surface(),
            );
            let o = ((y as usize) * (ow as usize) + x as usize) * 4;
            image.rgba[o..o + 4].copy_from_slice(&px);
        }
    }
    image
}

struct Sample {
    /// Reconstructed water depth (`pressure`), the field the wet sheen reads.
    depth: f32,
    deposited: [f32; super::palette::MAX_PIGMENTS],
    suspended: [f32; super::palette::MAX_PIGMENTS],
    /// Per pigment, the cubic blend of each of the 4x4 taps' soft-dilated
    /// `deposited + suspended * visibility`; see the module doc.
    presence: [f32; super::palette::MAX_PIGMENTS],
    /// The cubic blend of whether each tap is inside the paint, the largest
    /// over pigments: 1 in a body, 0 outside, its 0.5 contour the outline.
    /// See the module doc, "Composite modes".
    mask: f32,
}

/// Side of the cell window one sample reads: the 4x4 taps plus the ring
/// their 3x3 neighbourhoods reach.
const WINDOW: usize = 6;

/// Painted neighbours out of 8 that make a tap count as fully inside the paint.
const MASK_MAJORITY: u32 = 5;

/// Painted neighbours out of 8 at or below which a painted tap is a thin mark
/// (a lone dot, a line one cell wide, its end) and counts as fully inside.
/// Only a body's corners, with more, round off.
const MASK_THIN: u32 = 2;

/// The 3x3 presence kernel, row-major from the tap's top-left neighbour: a
/// Gaussian `2^(-d^2)` over squared cell distance, normalised to sum to 1.
/// Powers of two are exact in `f32`, so the shader port needs no `exp` and
/// weighs each neighbour identically. Summing to 1 makes the dilation
/// homogeneous: a uniform field keeps its own thickness, so the body of a
/// wash is untouched and only its boundary and its pits are filled.
const PRESENCE_KERNEL: [f32; 9] = [
    0.0625, 0.125, 0.0625, //
    0.125, 0.25, 0.125, //
    0.0625, 0.125, 0.0625,
];

/// The cubic B-spline weights for a fractional position `t in 0..1` between
/// sample points, for the four taps at `floor - 1 ..= floor + 2`. See the
/// module doc for the formula; all weights are in `0..1` and sum to 1.
fn cubic_weights(t: f32) -> [f32; 4] {
    let u = 1.0 - t;
    [
        u * u * u / 6.0,
        (3.0 * t * t * t - 6.0 * t * t + 4.0) / 6.0,
        (-3.0 * t * t * t + 3.0 * t * t + 3.0 * t + 1.0) / 6.0,
        t * t * t / 6.0,
    ]
}

/// Cubic B-spline sample at normalised `(u, v)`; the same lookup the shader
/// port performs on the sim buffers, evaluating the same 16 taps in the same
/// order.
fn cubic_sample(grid: &SimulationGrid, u: f32, v: f32, wet_visibility: f32) -> Sample {
    let w = grid.width as usize;
    let h = grid.height as usize;
    let fx = (u * w as f32 - 0.5).clamp(0.0, (w - 1) as f32);
    let fy = (v * h as f32 - 0.5).clamp(0.0, (h - 1) as f32);
    let x0 = fx.floor() as usize;
    let y0 = fy.floor() as usize;
    let tx = fx - x0 as f32;
    let ty = fy - y0 as f32;
    let wx = cubic_weights(tx);
    let wy = cubic_weights(ty);
    let n = grid.cell_count();
    let k_count = grid.pigment_count.min(super::palette::MAX_PIGMENTS);
    let mut out = Sample {
        depth: 0.0,
        deposited: [0.0; super::palette::MAX_PIGMENTS],
        suspended: [0.0; super::palette::MAX_PIGMENTS],
        presence: [0.0; super::palette::MAX_PIGMENTS],
        mask: 0.0,
    };
    let cell_presence = |k: usize, j: usize| {
        grid.pigments_deposited[k * n + j] + grid.pigments_in_water[k * n + j] * wet_visibility
    };
    // The cells the taps and their 3x3 neighbourhoods read, once: the 4x4
    // taps at `x0 - 1 ..= x0 + 2` widened by one on each side. Coordinates
    // clamp at the grid edge, as the taps do.
    let mut window = [[0.0f32; WINDOW]; WINDOW];
    let mut painted = [[false; WINDOW]; WINDOW];
    for k in 0..k_count {
        for (wy_i, row) in window.iter_mut().enumerate() {
            let ny = (y0 as isize + wy_i as isize - 2).clamp(0, h as isize - 1) as usize;
            for (wx_i, cell) in row.iter_mut().enumerate() {
                let nx = (x0 as isize + wx_i as isize - 2).clamp(0, w as isize - 1) as usize;
                *cell = cell_presence(k, ny * w + nx).max(0.0);
            }
        }
        for (row, flags) in window.iter().zip(painted.iter_mut()) {
            for (&q, flag) in row.iter().zip(flags.iter_mut()) {
                *flag = q > LUMINOUS_MASK_THICKNESS;
            }
        }
        let mut mask = 0.0f32;
        for (oy, &wyi) in wy.iter().enumerate() {
            for (ox, &wxo) in wx.iter().enumerate() {
                let wgt = wxo * wyi;
                // Presence at the tap: its own thickness raised toward its
                // neighbours by a Gaussian-weighted fourth-power mean. The mean
                // alone thins isolated cells and thin strokes; a plain maximum
                // flattens the whole 3x3 to one value, which gave the luminous
                // alpha cell-shaped plateaus. See the module doc,
                // "Reconstruction".
                let mut acc = 0.0f32;
                let mut neighbours = 0u32;
                let mut soft = 0.0f32;
                for dy in 0..3 {
                    for dx in 0..3 {
                        let q = window[oy + dy][ox + dx];
                        let q = q * q;
                        acc += PRESENCE_KERNEL[dy * 3 + dx] * q * q;
                        if painted[oy + dy][ox + dx] {
                            soft += PRESENCE_KERNEL[dy * 3 + dx];
                            if (dy, dx) != (1, 1) {
                                neighbours += 1;
                            }
                        }
                    }
                }
                let own = window[oy + 1][ox + 1];
                out.presence[k] += acc.sqrt().sqrt().max(own) * wgt;
                // How far inside the paint the tap is: the kernel-weighted share
                // of its 3x3 that is painted, so a staircase's corners round
                // off toward the line through them. A tap ringed by a painted
                // majority is fully inside (a bare cell in a body is a
                // pinhole). So is a painted tap with at most `MASK_THIN`
                // painted neighbours: a thin mark has no corner to round, and
                // the share would blur it away.
                let thin = painted[oy + 1][ox + 1] && neighbours <= MASK_THIN;
                let inside = if neighbours >= MASK_MAJORITY || thin {
                    1.0
                } else {
                    soft
                };
                mask += inside * wgt;
            }
        }
        out.mask = out.mask.max(mask);
    }
    for (oy, &wyi) in wy.iter().enumerate() {
        let tap_y = y0 as isize + oy as isize - 1;
        let cy = tap_y.clamp(0, h as isize - 1) as usize;
        for (ox, &wxo) in wx.iter().enumerate() {
            let tap_x = x0 as isize + ox as isize - 1;
            let cx = tap_x.clamp(0, w as isize - 1) as usize;
            let wgt = wxo * wyi;
            let i = cy * w + cx;
            out.depth += grid.pressure[i] * wgt;
            for k in 0..k_count {
                out.deposited[k] += grid.pigments_deposited[k * n + i] * wgt;
                out.suspended[k] += grid.pigments_in_water[k * n + i] * wgt;
            }
        }
    }
    out
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::domain::pigment::{Pigment, builtin};
    use crate::domain::{Paper, PaperField, Seed, SimulationGrid};

    #[test]
    fn zero_thickness_is_clear() {
        let (r, t) = layer(0.0, 0.0);
        assert_eq!((r, t), (0.0, 1.0));
        let (r, t) = layer_rgb(builtin::indigo().k, builtin::indigo().s, 0.0);
        assert_eq!(r, Rgb::new(0.0, 0.0, 0.0));
        assert_eq!(t, Rgb::new(1.0, 1.0, 1.0));
    }

    #[test]
    fn unit_thickness_over_white_recovers_design_reflectance() {
        let p = builtin::quinacridone_rose();
        let (r, t) = layer_rgb(p.k, p.s, 1.0);
        let over_white = composite(&[(r, t)], Rgb::new(1.0, 1.0, 1.0));
        for (c, expect) in over_white.0.iter().zip([0.72, 0.18, 0.36]) {
            assert!((c - expect).abs() < 0.02, "{c} vs {expect}");
        }
    }

    #[test]
    fn thick_layer_is_opaque_and_finite() {
        let p = builtin::lamp_black();
        let (r, t) = layer_rgb(p.k, p.s, 500.0);
        for c in 0..3 {
            assert!(r.0[c].is_finite() && t.0[c].is_finite());
            assert!(t.0[c] < 1e-6);
        }
    }

    fn totals(p: &Pigment, thickness: f32) -> MixTotals {
        MixTotals {
            kx: p.k.map(|k| k * thickness),
            sx: p.s.map(|s| s * thickness),
            thickness,
        }
    }

    /// Glaze thickness the alpha approximation is checked at.
    pub const THIN_GLAZE_THICKNESS: f32 = 0.15;
    /// Worst channel error over black across every built-in pigment at
    /// [`THIN_GLAZE_THICKNESS`]; over white the error is bounded per channel
    /// by `ALPHA_SOFTNESS * (mean - min)` of the returned light. The black
    /// error is the spread of per-channel transmittance folded into one
    /// alpha, largest for phthalo blue (red channel).
    pub const THIN_GLAZE_BLACK_TOLERANCE: f32 = 0.3;

    #[test]
    fn alpha_conversion_matches_km_for_thin_glaze() {
        let mut worst_black = 0.0f32;
        for p in builtin::all() {
            let (r, t) = layer_rgb(p.k, p.s, THIN_GLAZE_THICKNESS);
            let px = to_premultiplied(r, t);
            let white = Rgb::new(1.0, 1.0, 1.0);
            let exact_white = composite(&[(r, t)], white);
            let exact_black = composite(&[(r, t)], Rgb::new(0.0, 0.0, 0.0));
            let returned: Vec<f32> = (0..3)
                .map(|c| t.0[c] * t.0[c] / (1.0 - r.0[c]).max(1e-6))
                .collect();
            let min_ret = returned.iter().cloned().fold(f32::MAX, f32::min);
            let mean_ret = returned.iter().sum::<f32>() / 3.0;
            let white_bound = ALPHA_SOFTNESS * (mean_ret - min_ret) + 1e-4;
            for c in 0..3 {
                let over_white = px[c] + (1.0 - px[3]);
                let err = over_white - exact_white.0[c];
                assert!(
                    err >= -1e-4 && err <= white_bound,
                    "{} channel {c} over white: {over_white} vs {} (bound {white_bound})",
                    p.name,
                    exact_white.0[c]
                );
                let over_black = px[c];
                let err = (over_black - exact_black.0[c]).abs();
                worst_black = worst_black.max(err);
                assert!(
                    err < THIN_GLAZE_BLACK_TOLERANCE,
                    "{} channel {c} over black: {over_black} vs {}",
                    p.name,
                    exact_black.0[c]
                );
            }
        }
        eprintln!("worst over-black error at thin glaze: {worst_black}");
    }

    #[test]
    fn luminous_over_black_shows_the_on_white_colour_with_chroma() {
        // Over black the composited pixel is the premultiplied rgb = W * alpha,
        // so its chroma scales with coverage; the straight colour W carries the
        // hue itself. Both are checked: W at thickness 0.5, the pixel at 1.0.
        // The `for_dark_surface` variants are excluded on purpose: they are
        // built for Subtractive glow and their on-white colour is nearly white.
        let chroma = |rgb: [f32; 3]| {
            rgb.iter().cloned().fold(0.0, f32::max) - rgb.iter().cloned().fold(1.0, f32::min)
        };
        let pixel = |p: &Pigment, thickness: f32| {
            to_premultiplied_luminous(
                totals(p, thickness),
                totals(p, thickness),
                totals(p, thickness),
            )
        };
        let check = |p: &Pigment| {
            let px = pixel(p, 0.5);
            let straight = [px[0] / px[3], px[1] / px[3], px[2] / px[3]];
            let px1 = pixel(p, 1.0);
            eprintln!(
                "{}: straight W @0.5 {straight:?} (alpha {}), pixel @1.0 {:?}",
                p.name,
                px[3],
                &px1[..3]
            );
            assert!(chroma(straight) > 0.15, "{} straight chroma", p.name);
            assert!(
                chroma([px1[0], px1[1], px1[2]]) > 0.15,
                "{} pixel chroma",
                p.name
            );
        };
        check(&builtin::moon_gold());
        check(&builtin::quinacridone_rose());
        check(&builtin::phthalo_blue());
        check(&builtin::cerulean());
    }

    #[test]
    fn premultiplied_invariant_holds_in_both_modes() {
        for p in builtin::all() {
            for thickness in [0.02, 0.2, 0.6, 1.0, 3.0] {
                let textured = totals(&p, thickness * 1.4);
                let plain = totals(&p, thickness);
                for mode in [CompositeMode::Subtractive, CompositeMode::Luminous] {
                    let px = composite_pixel(textured, plain, plain, mode);
                    for c in 0..3 {
                        assert!(
                            px[c] <= px[3] + 1e-6,
                            "{} {mode:?} @ {thickness}: rgb {} > alpha {}",
                            p.name,
                            px[c],
                            px[3]
                        );
                        assert!(px[c] >= 0.0 && px[c].is_finite());
                    }
                }
            }
        }
    }

    #[test]
    fn luminous_matches_subtractive_over_white_for_thin_pale_layers() {
        let p = builtin::moon_gold();
        let m = totals(&p, 0.05);
        let sub = composite_pixel(m, m, m, CompositeMode::Subtractive);
        let lum = composite_pixel(m, m, m, CompositeMode::Luminous);
        for c in 0..3 {
            let over_white_sub = sub[c] + (1.0 - sub[3]);
            let over_white_lum = lum[c] + (1.0 - lum[3]);
            assert!(
                (over_white_sub - over_white_lum).abs() < 0.03,
                "channel {c}: {over_white_sub} vs {over_white_lum}"
            );
        }
    }

    #[test]
    fn luminous_alpha_is_flat_across_the_body_and_fades_at_the_edge() {
        let p = builtin::moon_gold();
        let alpha_at = |thickness: f32| {
            let m = totals(&p, thickness);
            composite_pixel(m, m, m, CompositeMode::Luminous)[3]
        };
        // Coverage of moon gold: ~0.03 at thickness 0.05, ~0.1 at 0.16,
        // ~0.26 at 0.5, ~0.45 at 1.
        assert!(alpha_at(0.02) < 0.05, "vanishing thickness fades out");
        let toe = alpha_at(0.16);
        assert!(toe > 0.05 && toe < 0.8, "halo band is partial: {toe}");
        assert_eq!(
            alpha_at(0.5),
            1.0,
            "body at half thickness is fully covered"
        );
        assert_eq!(alpha_at(1.0), 1.0);
        assert_eq!(alpha_at(3.0), 1.0);
        let mut prev = 0.0;
        for i in 0..=20 {
            let a = alpha_at(i as f32 * 0.05);
            assert!(a >= prev - 1e-6, "monotone");
            prev = a;
        }
    }

    #[test]
    fn luminous_colour_is_floored_for_thin_layers_and_darkens_with_thickness() {
        let p = builtin::quinacridone_rose();
        let straight = |thickness: f32| {
            let px = to_premultiplied_luminous(
                totals(&p, thickness),
                totals(&p, thickness),
                totals(&p, thickness),
            );
            [px[0] / px[3], px[1] / px[3], px[2] / px[3]]
        };
        let thin = straight(0.1);
        let at_floor = straight(LUMINOUS_COLOUR_FLOOR);
        let unit = straight(1.0);
        let dense = straight(2.5);
        for c in 0..3 {
            assert!(
                (thin[c] - at_floor[c]).abs() < 1e-4,
                "layers below the floor share the floor colour"
            );
        }
        let sum = |c: [f32; 3]| c.iter().sum::<f32>();
        assert!(sum(unit) < sum(at_floor), "pooling darkens above the floor");
        assert!(sum(dense) < sum(unit), "overlaps darken further");
    }

    #[test]
    fn luminous_texture_darkens_colour_but_leaves_alpha_alone() {
        let p = builtin::moon_gold();
        let plain = totals(&p, 0.4);
        let smooth = to_premultiplied_luminous(plain, plain, plain);
        let grainy = to_premultiplied_luminous(totals(&p, 0.4 * 1.5), plain, plain);
        let thin = to_premultiplied_luminous(totals(&p, 0.4 * 0.6), plain, plain);
        assert_eq!(smooth[3], grainy[3]);
        assert_eq!(smooth[3], thin[3]);
        let lum = |px: [f32; 4]| px[0] + px[1] + px[2];
        assert!(lum(grainy) < lum(smooth), "denser texture darkens");
        assert!(lum(thin) > lum(smooth), "sparser texture lightens");
        // The damping leaves the colour strictly between the smooth and the
        // fully textured result, in proportion to the strength.
        let full =
            to_premultiplied_luminous_with_strength(totals(&p, 0.4 * 1.5), plain, plain, 1.0);
        assert_eq!(full[3], grainy[3]);
        assert!(lum(full) < lum(grainy) && lum(grainy) < lum(smooth));
        let ratio = (lum(smooth) - lum(grainy)) / (lum(smooth) - lum(full));
        assert!(
            (ratio - LUMINOUS_GRAIN_STRENGTH).abs() < 0.12,
            "damping ratio {ratio} vs strength {LUMINOUS_GRAIN_STRENGTH}"
        );
        let sub_smooth = composite_pixel(plain, plain, plain, CompositeMode::Subtractive);
        let sub_grainy = composite_pixel(
            totals(&p, 0.4 * 1.5),
            plain,
            plain,
            CompositeMode::Subtractive,
        );
        assert_ne!(
            sub_smooth[3], sub_grainy[3],
            "subtractive still carries texture in alpha"
        );
    }

    /// A coarse grid under a much finer output: the band is four cells wide
    /// and its edges sweep two cells down the grid, so every row meets the
    /// lattice at a different sub-cell offset. Each cell is either bare or at
    /// full `thickness`, as a dried simulation deposit is: a body runs at its
    /// own thickness up to its last cell, so its edge is a staircase.
    fn slanted_band(cells: u32, out: u32, thickness: f32) -> (SimulationGrid, Palette, PaperField) {
        let palette = Palette::moonlight();
        let grid_paper = PaperField::generate(&Paper::hot_press(Seed(5)), cells, cells);
        let out_paper = PaperField::generate(&Paper::hot_press(Seed(5)), out, out);
        let mut grid = SimulationGrid::new(&grid_paper, palette.len())
            .with_composite_mode(CompositeMode::Luminous);
        for y in 0..cells {
            let lo = 10.0 + (y as f32 + 0.5) / cells as f32 * 2.0;
            for x in 0..cells {
                let covered = ((x + 1) as f32).min(lo + 4.0) - (x as f32).max(lo);
                if covered >= 0.5 {
                    grid.pigments_deposited[(y * cells + x) as usize] = thickness;
                }
            }
        }
        (grid, palette, out_paper)
    }

    #[test]
    fn luminous_outline_keeps_a_lone_painted_cell() {
        let (cells, out) = (32u32, 256u32);
        let params = RenderParams {
            granulation_gain: 0.0,
            wet_pigment_visibility: 0.0,
            ..RenderParams::default()
        };
        let palette = Palette::moonlight();
        let grid_paper = PaperField::generate(&Paper::hot_press(Seed(5)), cells, cells);
        let out_paper = PaperField::generate(&Paper::hot_press(Seed(5)), out, out);
        let mut grid = SimulationGrid::new(&grid_paper, palette.len())
            .with_composite_mode(CompositeMode::Luminous);
        grid.pigments_deposited[(16 * cells + 16) as usize] = 0.6;
        let image = render(&grid, &palette, &out_paper, &params);
        let centre = 16 * (out / cells) + out / cells / 2;
        let dot = image.pixel(centre, centre)[3];
        // Blended as a share of its 3x3, a lone cell read 0 here; as a thin
        // mark it keeps the 0.35 a plain painted flag gives it.
        assert!(dot >= 0.3, "a lone painted cell reads at {dot}");
    }

    #[test]
    fn luminous_outline_is_antialiased_over_half_a_cell_at_any_thickness() {
        let (cells, out) = (32u32, 256u32);
        let cell_px = (out / cells) as f32;
        let params = RenderParams {
            granulation_gain: 0.0,
            wet_pigment_visibility: 0.0,
            ..RenderParams::default()
        };
        // A pale glaze whose alpha never saturates, and a body well past
        // `LUMINOUS_ALPHA_FULL`. Without the mask the dense body rose from a
        // tenth of its alpha to nine tenths in 2 pixels here, a quarter of a
        // cell: a one-pixel edge tracing the staircase.
        for thickness in [0.06f32, 0.6] {
            let (grid, palette, out_paper) = slanted_band(cells, out, thickness);
            let image = render(&grid, &palette, &out_paper, &params);
            for y in 0..out {
                let row: Vec<f32> = (0..out).map(|x| image.pixel(x, y)[3]).collect();
                let peak = row.iter().cloned().fold(0.0f32, f32::max);
                let crest = row.iter().position(|&a| a >= peak - 1e-6).unwrap();
                for w in row[..=crest].windows(2) {
                    assert!(
                        w[1] >= w[0] - 1e-6,
                        "alpha falls back inside the ramp at row {y}, thickness {thickness}"
                    );
                }
                let toe = row.iter().position(|&a| a >= 0.1 * peak).unwrap();
                let shoulder = row.iter().position(|&a| a >= 0.9 * peak).unwrap();
                let width = (shoulder - toe) as f32;
                assert!(
                    width >= 0.45 * cell_px,
                    "row {y}, thickness {thickness}: edge climbs in {width} px of an {cell_px} px cell"
                );
            }
        }
    }

    #[test]
    fn luminous_presence_keeps_a_pitted_body_continuous() {
        let palette = Palette::moonlight();
        let (cells, out) = (32u32, 256u32);
        let scale = out / cells;
        let grid_paper = PaperField::generate(&Paper::hot_press(Seed(3)), cells, cells);
        let out_paper = PaperField::generate(&Paper::hot_press(Seed(3)), out, out);
        let mut grid = SimulationGrid::new(&grid_paper, palette.len())
            .with_composite_mode(CompositeMode::Luminous);
        for y in 8..24 {
            for x in 8..24 {
                grid.pigments_deposited[(y * cells + x) as usize] = 0.06;
            }
        }
        // Single-cell pinholes, the paper tooth's bare cells.
        let pits = [(12u32, 13u32), (16, 16), (19, 11), (13, 20)];
        for &(x, y) in &pits {
            grid.pigments_deposited[(y * cells + x) as usize] = 0.0;
        }
        let params = RenderParams {
            granulation_gain: 0.0,
            wet_pigment_visibility: 0.0,
            ..RenderParams::default()
        };
        let image = render(&grid, &palette, &out_paper, &params);
        let alpha_at = |cx: f32, cy: f32| {
            image.pixel((cx * scale as f32) as u32, (cy * scale as f32) as u32)[3]
        };
        for &(x, y) in &pits {
            let (cx, cy) = (x as f32 + 0.5, y as f32 + 0.5);
            let ring = [(-2.0, 0.0), (2.0, 0.0), (0.0, -2.0), (0.0, 2.0)]
                .iter()
                .map(|(dx, dy)| alpha_at(cx + dx, cy + dy))
                .sum::<f32>()
                / 4.0;
            let pit = alpha_at(cx, cy);
            assert!(
                pit >= 0.8 * ring,
                "pit at ({x},{y}) reads {pit} against {ring} around it"
            );
        }
    }

    #[test]
    fn luminous_presence_closes_pinholes_but_not_the_outside() {
        use crate::domain::{Paper, PaperField, Seed, SimulationGrid};
        let palette = Palette::moonlight();
        let field = PaperField::generate(&Paper::hot_press(Seed(1)), 32, 32);
        let mut grid = SimulationGrid::new(&field, palette.len());
        let n = grid.cell_count();
        for y in 8..24 {
            for x in 8..24 {
                grid.pigments_deposited[3 * n + y * 32 + x] = 0.6;
            }
        }
        grid.pigments_deposited[3 * n + 16 * 32 + 16] = 0.0;
        let params = RenderParams::default();
        let sub = render(&grid, &palette, &field, &params);
        let lum = render(
            &grid.clone().with_composite_mode(CompositeMode::Luminous),
            &palette,
            &field,
            &params,
        );
        let hole = lum.pixel(16, 16);
        assert!(
            hole[3] > 0.95,
            "pinhole is closed in luminous alpha: {}",
            hole[3]
        );
        // The cubic reconstruction smooths the single-cell hole in the
        // deposit to a thin spot (the 4/6-weight tap sits on the bare cell
        // against 1/6-weight taps on its 0.6 neighbours), so subtractive
        // keeps it as a visible dip rather than a full hole.
        assert!(
            sub.pixel(16, 16)[3] < sub.pixel(10, 10)[3] - 0.05,
            "subtractive keeps the pinhole as a dip"
        );
        assert_eq!(lum.pixel(2, 2)[3], 0.0, "outside stays transparent");
        assert!(
            lum.pixel(4, 16)[3] < 0.05,
            "four cells outside the body stays transparent"
        );
    }

    #[test]
    fn cubic_weights_are_a_partition_of_unity() {
        for t in [0.0, 0.125, 0.25, 0.375, 0.5, 0.75, 0.999] {
            let w = cubic_weights(t);
            assert!(w.iter().all(|&x| x >= 0.0), "non-negative at {t}: {w:?}");
            let sum: f32 = w.iter().sum();
            assert!((sum - 1.0).abs() < 1e-5, "weights sum to 1 at {t}: {sum}");
        }
    }

    #[test]
    fn cubic_reconstruction_recovers_a_constant_field() {
        use crate::domain::{Paper, PaperField, Seed, SimulationGrid};
        let palette = Palette::water();
        let field = PaperField::generate(&Paper::hot_press(Seed(2)), 16, 16);
        let mut grid = SimulationGrid::new(&field, palette.len());
        for i in 0..grid.cell_count() {
            grid.pigments_deposited[i] = 0.5;
        }
        let params = RenderParams {
            granulation_gain: 0.0,
            wet_pigment_visibility: 0.0,
            ..RenderParams::default()
        };
        let image = render(&grid, &palette, &field, &params);
        let mix = mixed_totals(&palette, &[params.optical(0.5), 0.0, 0.0, 0.0]);
        // Every output pixel reads the same uniform cell values, so the
        // reconstruction (a convex combination) reproduces the single-pixel
        // result of the uniform mix to f32 precision.
        let expect = composite_pixel_tuned(
            mix,
            mix,
            mix,
            mix,
            1.0,
            CompositeMode::Subtractive,
            &LUMINOUS_TUNING,
            WetLook::OFF,
            params.surface(),
        );
        for y in 0..16 {
            for x in 0..16 {
                let px = image.pixel(x, y);
                for c in 0..4 {
                    assert!(
                        (px[c] - expect[c]).abs() < 1e-4,
                        "({x},{y}) channel {c}: {} vs {}",
                        px[c],
                        expect[c]
                    );
                }
            }
        }
    }

    #[test]
    fn two_glazes_are_darker_than_one() {
        let palette = Palette::water();
        let one = composite(
            &[mixed_layer(&palette, &[0.5, 0.0, 0.0, 0.0])],
            Rgb::new(1.0, 1.0, 1.0),
        );
        let two = composite(
            &[mixed_layer(&palette, &[0.5, 0.5, 0.0, 0.0])],
            Rgb::new(1.0, 1.0, 1.0),
        );
        assert!(two.mean() < one.mean());
    }

    /// A sheet with one phthalo-blue region deposited and no water, so the
    /// sheen is the only thing that can differ between renders.
    fn sheen_grid(seed: u64) -> (SimulationGrid, Palette, PaperField) {
        let palette = Palette::water();
        let field = PaperField::generate(&Paper::hot_press(Seed(seed)), 32, 32);
        let mut grid = SimulationGrid::new(&field, palette.len());
        let n = grid.cell_count();
        for y in 8..24 {
            for x in 8..24 {
                grid.pigments_deposited[n + y * 32 + x] = 0.6;
            }
        }
        (grid, palette, field)
    }

    #[test]
    fn a_dry_frame_is_unchanged_by_the_wet_look() {
        let (grid, palette, field) = sheen_grid(11);
        let render_at = |darken: f32| {
            render(
                &grid,
                &palette,
                &field,
                &RenderParams {
                    wet_darken: darken,
                    ..RenderParams::default()
                },
            )
        };
        let off = render_at(0.0);
        let on = render_at(0.12);
        assert_eq!(
            off.rgba, on.rgba,
            "zero depth leaves the frame bit-identical"
        );
    }

    #[test]
    fn wet_paint_reads_deeper_than_the_same_paint_dry() {
        let (grid, palette, field) = sheen_grid(12);
        let params = RenderParams {
            granulation_gain: 0.0,
            ..RenderParams::default()
        };
        for mode in [CompositeMode::Subtractive, CompositeMode::Luminous] {
            let mut dry = grid.clone();
            dry.pressure.fill(0.0);
            dry.wet.fill(0.0);
            dry.composite_mode = mode;
            let mut wet = grid.clone();
            wet.pressure.fill(params.sheen_depth);
            wet.wet.fill(1.0);
            wet.composite_mode = mode;
            let dry_img = render(&dry, &palette, &field, &params);
            let wet_img = render(&wet, &palette, &field, &params);
            for y in 8..24 {
                for x in 8..24 {
                    let d = dry_img.pixel(x, y);
                    let w = wet_img.pixel(x, y);
                    let lum = |p: [f32; 4]| p[0] + p[1] + p[2];
                    assert!(
                        lum(w) < lum(d),
                        "{mode:?} ({x},{y}): wet {:?} not darker than dry {:?}",
                        &w[..3],
                        &d[..3]
                    );
                    assert_eq!(
                        w[3], d[3],
                        "{mode:?} ({x},{y}): sheen changed coverage {} vs {}",
                        w[3], d[3]
                    );
                }
            }
        }
    }

    #[test]
    fn the_sheen_lifts_gradually_with_the_film() {
        let (grid, palette, field) = sheen_grid(13);
        let params = RenderParams {
            granulation_gain: 0.0,
            ..RenderParams::default()
        };
        let sheet_lum = |depth: f32| {
            let mut g = grid.clone();
            g.pressure.fill(depth);
            g.wet.fill(1.0);
            let img = render(&g, &palette, &field, &params);
            let mut sum = 0.0f64;
            for y in 8..24 {
                for x in 8..24 {
                    let px = img.pixel(x, y);
                    sum += f64::from(px[0] + px[1] + px[2]);
                }
            }
            sum
        };
        let mut prev = sheet_lum(0.0);
        for &depth in &[0.05, 0.1, 0.2, 0.35, 0.5, 0.8, 1.0] {
            let at = sheet_lum(depth);
            if depth <= params.sheen_depth {
                assert!(at < prev, "not monotone at depth {depth}: {at} >= {prev}");
            } else {
                assert_eq!(
                    at, prev,
                    "sheen must saturate at sheen_depth; depth {depth}"
                );
            }
            prev = at;
        }
    }

    #[test]
    fn optical_thickness_saturates_with_headroom_past_the_swatch() {
        let p = RenderParams::default();
        assert_eq!(p.optical(0.0), 0.0);
        let mut prev = 0.0;
        for i in 1..=40 {
            let t = p.optical(i as f32 * 0.05);
            assert!(t > prev, "monotone at {}", i as f32 * 0.05);
            assert!(t < p.optical_max);
            prev = t;
        }
        // A dried light wash sits near its swatch (thickness 1), a full
        // deposit reads well past it, a faint halo stays faint.
        let light = p.optical(0.4);
        assert!((0.75..1.1).contains(&light), "light wash at {light}");
        assert!(p.optical(1.0) > 1.3, "full deposit at {}", p.optical(1.0));
        assert!(p.optical(0.02) < 0.03, "halo at {}", p.optical(0.02));
    }

    #[test]
    fn the_surface_correction_deepens_paint_and_keeps_the_pixel_well_formed() {
        let surface = RenderParams::default().surface();
        let bare = totals(&builtin::indigo(), 0.0);
        let px = composite_pixel_tuned(
            bare,
            bare,
            bare,
            bare,
            1.0,
            CompositeMode::Subtractive,
            &LUMINOUS_TUNING,
            WetLook::OFF,
            surface,
        );
        assert_eq!(px, [0.0, 0.0, 0.0, 0.0], "bare paper is untouched");
        for p in builtin::all() {
            for thickness in [0.05, 0.3, 1.0, 2.0] {
                let m = totals(&p, thickness);
                for mode in [CompositeMode::Subtractive, CompositeMode::Luminous] {
                    let plain = composite_pixel(m, m, m, mode);
                    let px = composite_pixel_tuned(
                        m,
                        m,
                        m,
                        m,
                        1.0,
                        mode,
                        &LUMINOUS_TUNING,
                        WetLook::OFF,
                        surface,
                    );
                    let over_white = |q: [f32; 4]| [0, 1, 2].map(|c| q[c] + 1.0 - q[3]);
                    let (a, b) = (over_white(plain), over_white(px));
                    for c in 0..3 {
                        assert!(px[c] <= px[3] + 1e-6 && px[c] >= 0.0, "{} {mode:?}", p.name);
                        assert!(
                            b[c] <= a[c] + 1e-5,
                            "{} {mode:?} @ {thickness}: lighter",
                            p.name
                        );
                    }
                    if thickness >= 1.0 {
                        assert!(
                            b.iter().sum::<f32>() < a.iter().sum::<f32>() - 0.02,
                            "{} {mode:?} @ {thickness}: not deepened",
                            p.name
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn paint_reads_at_nearly_its_dried_depth_the_moment_it_lands() {
        let (grid, palette, field) = sheen_grid(14);
        let params = RenderParams {
            granulation_gain: 0.0,
            ..RenderParams::default()
        };
        let n = grid.cell_count();
        let mut wet = grid.clone();
        for i in 0..n {
            wet.pigments_in_water[n + i] = wet.pigments_deposited[n + i];
            wet.pigments_deposited[n + i] = 0.0;
        }
        wet.pressure.fill(params.sheen_depth);
        wet.wet.fill(1.0);
        let dry_img = render(&grid, &palette, &field, &params);
        let wet_img = render(&wet, &palette, &field, &params);
        let depth = |p: [f32; 4]| 3.0 - (p[0] + p[1] + p[2] + 3.0 * (1.0 - p[3]));
        let (d, w) = (depth(dry_img.pixel(16, 16)), depth(wet_img.pixel(16, 16)));
        assert!(w >= 0.9 * d, "fresh paint depth {w} against dried {d}");
    }
}
