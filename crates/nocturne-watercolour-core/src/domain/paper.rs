//! Paper: a deterministic height field plus capillary capacity, generated
//! from a seed with hash-based value noise and a directional fibre term. The
//! field is defined in normalised `0..1` coordinates and sampled at whatever
//! resolution the caller asks for, so the simulation grid and the output
//! image see the same paper.

use super::scene::isotropic_scale;
use super::seed::{Seed, SubSeed, hash2};

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Paper {
    pub seed: Seed,
    /// Base grain cells across the unit square. Larger is finer.
    pub grain_scale: f32,
    /// Peak-to-peak height variation, in the `0..1` height units the sim uses.
    pub height_amplitude: f32,
    /// Capillary capacity range mapped onto the height field: `[min, max]`.
    pub absorbency: [f32; 2],
    /// `0` is isotropic grain, `1` is strongly directional fibre.
    pub fibre_anisotropy: f32,
}

impl Paper {
    pub fn cold_press(seed: Seed) -> Paper {
        Paper {
            seed: seed.derive(SubSeed::Paper),
            grain_scale: 96.0,
            height_amplitude: 0.7,
            absorbency: [0.3, 0.8],
            fibre_anisotropy: 0.12,
        }
    }

    pub fn rough(seed: Seed) -> Paper {
        Paper {
            seed: seed.derive(SubSeed::Paper),
            grain_scale: 56.0,
            height_amplitude: 0.9,
            absorbency: [0.4, 1.0],
            fibre_anisotropy: 0.2,
        }
    }

    pub fn hot_press(seed: Seed) -> Paper {
        Paper {
            seed: seed.derive(SubSeed::Paper),
            grain_scale: 160.0,
            height_amplitude: 0.25,
            absorbency: [0.2, 0.5],
            fibre_anisotropy: 0.5,
        }
    }
}

/// Sampled paper at one resolution. `height` is centred on `0.5`; `capacity`
/// is the capillary capacity `c` of each cell; `aspect` is the output aspect
/// the field was generated for (see [`isotropic_scale`]).
#[derive(Debug, Clone, PartialEq)]
pub struct PaperField {
    pub width: u32,
    pub height_px: u32,
    pub height: Vec<f32>,
    pub capacity: Vec<f32>,
    pub aspect: f32,
}

impl PaperField {
    /// [`PaperField::generate_with_aspect`] for a square output.
    pub fn generate(paper: &Paper, width: u32, height: u32) -> PaperField {
        Self::generate_with_aspect(paper, width, height, 1.0)
    }

    /// Samples the paper over a `width x height` grid that will be stretched
    /// to an output of the given aspect, so the grain is isotropic after the
    /// stretch. Both the simulation grid and the render-resolution paper of
    /// one scene must use the scene's aspect (`Scene::aspect`).
    pub fn generate_with_aspect(paper: &Paper, width: u32, height: u32, aspect: f32) -> PaperField {
        Self::generate_with_pixel_scale(paper, width, height, aspect, 0.0)
    }

    /// [`PaperField::generate_with_aspect`] for a render target, sampling the
    /// noise through [`octave_band`] at `pixel_scale` so octaves finer than a
    /// few output pixels fade out instead of reading as per-pixel speckle. The
    /// band window grows with the output's long edge ([`grain_band_window`]),
    /// so a large canvas drops the octaves that fall at a few pixels while a
    /// small canvas keeps its base tooth. The simulation paper keeps full
    /// octaves via `generate_with_aspect` (whose `pixel_scale` is the sim
    /// cell), so the fluid rules and the CPU<->GPU sim state are unchanged.
    pub fn generate_with_pixel_scale(
        paper: &Paper,
        width: u32,
        height: u32,
        aspect: f32,
        pixel_scale: f32,
    ) -> PaperField {
        let n = (width as usize) * (height as usize);
        let mut h = Vec::with_capacity(n);
        let mut cap = Vec::with_capacity(n);
        let inv_w = 1.0 / width.max(1) as f32;
        let inv_h = 1.0 / height.max(1) as f32;
        let bands = OctaveBands::new(
            paper,
            pixel_scale,
            grain_band_window(width.max(height) as f32),
        );
        for y in 0..height {
            for x in 0..width {
                let u = (x as f32 + 0.5) * inv_w;
                let v = (y as f32 + 0.5) * inv_h;
                let value = sample_height_banded(paper, u, v, aspect, &bands);
                h.push(value);
                let t = ((value - 0.5) / paper.height_amplitude.max(1e-3) + 0.5).clamp(0.0, 1.0);
                cap.push(paper.absorbency[0] + (paper.absorbency[1] - paper.absorbency[0]) * t);
            }
        }
        PaperField {
            width,
            height_px: height,
            height: h,
            capacity: cap,
            aspect,
        }
    }

    pub fn len(&self) -> usize {
        self.height.len()
    }

    pub fn is_empty(&self) -> bool {
        self.height.is_empty()
    }
}

/// Weight of the low-frequency pooling octaves in the height field; the
/// fine grain takes the rest. Comparable amplitudes are what make broad
/// pools read against the tooth.
pub const POOL_WEIGHT: f32 = 0.6;

/// Base band-limit window for the render-resolution paper at a 256-pixel
/// output; [`grain_band_window`] scales it with the output's long edge.
/// Granulation is sampled at output resolution, so on a large canvas the
/// finest octaves fall at one to two output pixels and modulate every pixel
/// independently: that reads as per-pixel speckle. Each noise term keeps full
/// weight while its period in output pixels is at least [`GRAIN_FULL_PERIOD_PX`]
/// and fades out below [`GRAIN_MIN_PERIOD_PX`], so the finest visible grain is
/// never under about three output pixels. The bounds are periods, not
/// frequencies, so the same octave is attenuated only when the canvas is
/// large enough that its period would be sub-pixel; the two-octave pooling
/// (at least ~26 px on a 256 output) is never touched. On a 256 canvas the
/// window sits just under the base octave's period (2.7 px), so a small
/// canvas keeps its tooth while a large one drops the octaves that fall at a
/// few pixels.
pub const GRAIN_MIN_PERIOD_PX: f32 = 2.0;
pub const GRAIN_FULL_PERIOD_PX: f32 = 3.0;

/// Band-limit window for an output whose long edge is `long_edge_px`: the
/// min period is `max(2, long_edge/256)` output pixels and the full period
/// `1.5x` that, so 256 and 512 outputs keep the base 2/3 px window, 768 gets
/// 3/4.5 px, 1024 gets 4/6 px, 2048 gets 8/12 px. A large canvas pushes the
/// octaves that fall at a few pixels below the window; a small canvas keeps
/// its base tooth.
pub fn grain_band_window(long_edge_px: f32) -> (f32, f32) {
    let min = GRAIN_MIN_PERIOD_PX.max(long_edge_px / 256.0);
    (min, 1.5 * min)
}

/// Size of one output pixel in the isotropic scene metric of `aspect`, for a
/// `width x height` output: the longer axis spans `max(aspect, 1/aspect)`
/// units across `max(width, height)` pixels. Both backends compute the render
/// paper's `pixel_scale` from this, so CPU and GPU band-limit identically.
pub fn render_pixel_scale(width: u32, height: u32, aspect: f32) -> f32 {
    let (ax, ay) = isotropic_scale(aspect);
    (ax / width.max(1) as f32).max(ay / height.max(1) as f32)
}

/// Smooth weight for a noise term of `freq` cycles per isotropic scene unit
/// sampled at one `pixel_scale` (isotropic units per output pixel): `1` from
/// `full_period_px` output pixels up, `0` at and below `min_period_px`, a
/// Hermite smoothstep on log period between. The window is
/// [`grain_band_window`]'s, so it grows with the output size. `pixel_scale
/// <= 0` keeps full weight everywhere (the simulation paper, whose cell size
/// is its own scale, keeps all octaves so the fluid rules are unchanged).
pub fn octave_band(pixel_scale: f32, min_period_px: f32, full_period_px: f32, freq: f32) -> f32 {
    if pixel_scale <= 0.0 {
        return 1.0;
    }
    let period_px = 1.0 / (freq * pixel_scale).max(1e-6);
    let lo = min_period_px.max(1e-6).ln();
    let hi = full_period_px.max(lo + 1e-6).ln();
    let t = ((period_px.ln() - lo) / (hi - lo)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// Height at normalised `(u, v)`: four octaves of fine grain, two
/// low-frequency pooling octaves (about 10 and 20 grain cells across) and a
/// fibre term stretched along `x`, all in `0.5 ± amplitude/2`. No
/// band-limiting (the simulation metric).
pub fn sample_height(paper: &Paper, u: f32, v: f32) -> f32 {
    sample_height_aspect(
        paper,
        u,
        v,
        1.0,
        0.0,
        (GRAIN_MIN_PERIOD_PX, GRAIN_FULL_PERIOD_PX),
    )
}

/// [`sample_height`] in the isotropic metric of an output with `aspect`
/// (see [`isotropic_scale`]); `(u, v)` stay normalised scene coordinates.
/// `pixel_scale` and `band` limit the fine octaves and the fibre via
/// [`octave_band`]; the pooling octaves are too coarse to ever be limited at
/// a valid `grain_scale` (`>= 10` grain cells across, so at least ~26 px on a
/// 256 output).
pub fn sample_height_aspect(
    paper: &Paper,
    u: f32,
    v: f32,
    aspect: f32,
    pixel_scale: f32,
    band: (f32, f32),
) -> f32 {
    sample_height_banded(
        paper,
        u,
        v,
        aspect,
        &OctaveBands::new(paper, pixel_scale, band),
    )
}

/// The [`octave_band`] weights of one field's noise terms, which depend on
/// the paper and the output alone: computed once per field, not per pixel.
struct OctaveBands {
    grain: [f32; 4],
    fibre: f32,
}

impl OctaveBands {
    fn new(paper: &Paper, pixel_scale: f32, band: (f32, f32)) -> OctaveBands {
        let (min_px, full_px) = band;
        let base = paper.grain_scale.max(1.0);
        let mut grain = [0.0; 4];
        let mut freq = base;
        for weight in &mut grain {
            *weight = octave_band(pixel_scale, min_px, full_px, freq);
            freq *= 2.3;
        }
        OctaveBands {
            grain,
            fibre: octave_band(pixel_scale, min_px, full_px, base * 1.6),
        }
    }
}

fn sample_height_banded(paper: &Paper, u: f32, v: f32, aspect: f32, bands: &OctaveBands) -> f32 {
    let (ax, ay) = isotropic_scale(aspect);
    let (u, v) = (u * ax, v * ay);
    let seed = paper.seed.0;
    let base = paper.grain_scale.max(1.0);
    // The noise is 0..1 centred on 0.5; the deviations are divided by the full
    // octave weight so a cut octave leaves the surviving coarse tooth at its
    // exact original amplitude (renormalising by the surviving weight would
    // amplify it, making the paper resolution-dependent).
    let full_weight = (1.0 - 0.55_f32.powi(4)) / (1.0 - 0.55);
    let mut deviation = 0.0;
    let mut amp = 1.0;
    let mut freq = base;
    for (octave, &band) in (0..4u64).zip(&bands.grain) {
        deviation += amp
            * band
            * (value_noise(seed.wrapping_add(octave * 0x1F1F), u * freq, v * freq) - 0.5);
        amp *= 0.55;
        freq *= 2.3;
    }
    let grain = 0.5 + deviation / full_weight;
    let pool = (value_noise(seed ^ 0x3A7C, u * base * 0.1, v * base * 0.1)
        + 0.7
            * value_noise(
                seed ^ 0x5C21,
                u * base * 0.05 + 0.37,
                v * base * 0.05 + 0.11,
            ))
        / 1.7;
    let fibre =
        0.5 + (value_noise(seed ^ 0xF1B7, u * base * 0.12, v * base * 1.6) - 0.5) * bands.fibre;
    let body = grain * (1.0 - POOL_WEIGHT) + pool * POOL_WEIGHT;
    let fibre_w = paper.fibre_anisotropy * 0.6;
    // The fibre's cross-streak period is its fine axis (`base * 1.6`); the
    // long-axis variation it shares is far too coarse to limit.
    let mixed = body * (1.0 - fibre_w) + fibre * fibre_w;
    0.5 + (mixed - 0.5) * paper.height_amplitude
}

fn value_noise(seed: u64, x: f32, y: f32) -> f32 {
    let xi = x.floor();
    let yi = y.floor();
    let fx = smooth(x - xi);
    let fy = smooth(y - yi);
    let (xi, yi) = (xi as i32, yi as i32);
    let a = hash2(seed, xi, yi);
    let b = hash2(seed, xi + 1, yi);
    let c = hash2(seed, xi, yi + 1);
    let d = hash2(seed, xi + 1, yi + 1);
    let top = a + (b - a) * fx;
    let bottom = c + (d - c) * fx;
    top + (bottom - top) * fy
}

fn smooth(t: f32) -> f32 {
    t * t * (3.0 - 2.0 * t)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_seed_same_field_different_seed_different() {
        let a = PaperField::generate(&Paper::cold_press(Seed(1)), 64, 48);
        let b = PaperField::generate(&Paper::cold_press(Seed(1)), 64, 48);
        let c = PaperField::generate(&Paper::cold_press(Seed(2)), 64, 48);
        assert_eq!(a, b);
        assert_ne!(a, c);
    }

    /// Lag (in samples) at which the normalised autocorrelation of `rows`
    /// along the row axis first drops below one half.
    fn correlation_length(values: &[f32], width: usize, height: usize, along_x: bool) -> f32 {
        let mean = values.iter().sum::<f32>() / values.len() as f32;
        let var = values.iter().map(|v| (v - mean).powi(2)).sum::<f32>() / values.len() as f32;
        let at = |x: usize, y: usize| values[y * width + x] - mean;
        let max_lag = if along_x { width / 2 } else { height / 2 };
        let mut prev = 1.0f32;
        for lag in 1..max_lag {
            let mut acc = 0.0;
            let mut count = 0;
            for y in 0..height {
                for x in 0..width {
                    let (x2, y2) = if along_x { (x + lag, y) } else { (x, y + lag) };
                    if x2 < width && y2 < height {
                        acc += at(x, y) * at(x2, y2);
                        count += 1;
                    }
                }
            }
            let corr = acc / (count as f32 * var);
            if corr < 0.5 {
                let t = (prev - 0.5) / (prev - corr).max(1e-6);
                return (lag - 1) as f32 + t;
            }
            prev = corr;
        }
        max_lag as f32
    }

    #[test]
    fn aspect_aware_paper_has_isotropic_grain_after_the_stretch() {
        // 512x128 pixels is a 4:1 output; sampled with aspect 4 the grain
        // must have the same correlation length in pixels on both axes,
        // whereas the square-metric field is stretched four times along x.
        let paper = Paper::cold_press(Seed(11));
        let iso = PaperField::generate_with_aspect(&paper, 512, 128, 4.0);
        let lx = correlation_length(&iso.height, 512, 128, true);
        let ly = correlation_length(&iso.height, 512, 128, false);
        assert!(
            (lx / ly - 1.0).abs() < 0.25,
            "isotropic field: x length {lx} vs y length {ly}"
        );
        let stretched = PaperField::generate(&paper, 512, 128);
        let sx = correlation_length(&stretched.height, 512, 128, true);
        let sy = correlation_length(&stretched.height, 512, 128, false);
        assert!(sx / sy > 2.5, "square-metric field: x {sx} vs y {sy}");
        assert_eq!(iso.aspect, 4.0);
    }

    #[test]
    fn height_and_capacity_are_bounded() {
        let p = Paper::rough(Seed(9));
        let f = PaperField::generate(&p, 96, 96);
        for (&h, &c) in f.height.iter().zip(&f.capacity) {
            assert!((0.0..=1.0).contains(&h));
            assert!(c >= p.absorbency[0] && c <= p.absorbency[1]);
        }
    }

    #[test]
    fn octave_band_is_one_beyond_full_period_and_zero_below_min() {
        // period 6 -> 1, period 2.5 -> 0, and monotone between; the neutral
        // `pixel_scale = 0` (simulation paper) keeps everything. Uses the base
        // 2/3 px window of a 256 output.
        let (lo, hi) = grain_band_window(256.0);
        assert_eq!(grain_band_window(256.0), (2.0, 3.0));
        assert_eq!(octave_band(0.0, lo, hi, 96.0), 1.0);
        assert_eq!(octave_band(1.0 / 1024.0, lo, hi, 96.0), 1.0); // 10.67 px period
        assert_eq!(octave_band(1.0 / 256.0, lo, hi, 507.84), 0.0); // 0.5 px period
        let mid = octave_band(1.0 / 512.0, lo, hi, 220.8); // 2.32 px period
        assert!(mid > 0.0 && mid < 1.0);
        let coarser = octave_band(1.0 / 1024.0, lo, hi, 220.8); // 4.64 px period
        assert!(coarser > mid, "longer period keeps more weight");
    }

    #[test]
    fn grain_band_window_scales_with_the_output_long_edge() {
        // min = max(2, long_edge/256), full = 1.5x min: 256 and 512 outputs
        // keep the base 2/3 px window, 768 gets 3/4.5 px, 1024 gets 4/6 px,
        // 2048 gets 8/12 px.
        assert_eq!(grain_band_window(256.0), (2.0, 3.0));
        assert_eq!(grain_band_window(512.0), (2.0, 3.0));
        assert_eq!(grain_band_window(768.0), (3.0, 4.5));
        assert_eq!(grain_band_window(1024.0), (4.0, 6.0));
        assert_eq!(grain_band_window(2048.0), (8.0, 12.0));
    }

    #[test]
    fn grain_band_window_reaches_into_the_4px_octave_at_1024_only() {
        // The octave at ~4.6 px (cold-press octave 1 at 1024) keeps full weight
        // under the base 2/3 px window but fades to about a third under the
        // 4/6 px window, while the same octave at 512 (2.3 px) is already cut.
        let (lo_256, hi_256) = grain_band_window(256.0);
        let (lo_1024, hi_1024) = grain_band_window(1024.0);
        let at = |ps: f32, lo: f32, hi: f32| octave_band(ps, lo, hi, 220.8);
        let w256 = at(1.0 / 256.0, lo_256, hi_256); // 1.16 px period
        let w512 = at(1.0 / 512.0, lo_256, hi_256); // 2.32 px period
        let w1024_old = at(1.0 / 1024.0, lo_256, hi_256); // 4.64 px period, old window
        let w1024_new = at(1.0 / 1024.0, lo_1024, hi_1024); // 4.64 px, new window
        assert_eq!(w256, 0.0);
        assert!(w1024_old > 0.999, "old window keeps the 4.6 px octave");
        assert!(
            w1024_new < w1024_old * 0.5,
            "new window attenuates it: {w1024_new} vs {w1024_old}"
        );
        assert!(w512 < 1.0, "512 still touches its 2.3 px octave");
    }

    #[test]
    fn render_pixel_scale_is_one_pixel_in_isotropic_units() {
        assert_eq!(render_pixel_scale(1024, 1024, 1.0), 1.0 / 1024.0);
        // A 1024x256 output at 4:1 spans 4 isotropic units on x and 1 on y,
        // so one pixel is 1/256 isotropic units on both axes.
        assert!((render_pixel_scale(1024, 256, 4.0) - 1.0 / 256.0).abs() < 1e-6);
        assert_eq!(render_pixel_scale(256, 1024, 0.25), 1.0 / 256.0);
    }

    #[test]
    fn band_limited_render_paper_drops_high_frequency_but_keeps_pools() {
        // At 1024 output the 4/6 px window cuts octaves 2 and 3 (2.0 and 0.9 px
        // periods) entirely and fades octave 1 (4.6 px) to about a third, so
        // the fine tooth that reads as per-pixel speckle loses most of its
        // adjacent-pixel contrast; the coarse octave and the pooling, which
        // carry the granulation's perceived strength, survive.
        let paper = Paper::cold_press(Seed(3));
        let full = PaperField::generate_with_pixel_scale(&paper, 1024, 1024, 1.0, 0.0);
        let limited = PaperField::generate_with_pixel_scale(
            &paper,
            1024,
            1024,
            1.0,
            render_pixel_scale(1024, 1024, 1.0),
        );
        let adjacent = |f: &PaperField| {
            let mut acc = 0.0;
            for y in 0..1024usize {
                for x in 0..1023usize {
                    acc += (f.height[y * 1024 + x] - f.height[y * 1024 + x + 1]).abs();
                }
            }
            acc / (1024.0 * 1023.0)
        };
        let (adj_full, adj_limited) = (adjacent(&full), adjacent(&limited));
        assert!(
            adj_limited < adj_full * 0.7,
            "fine tooth cut: {adj_limited} vs {adj_full}"
        );
        let at = |f: &PaperField, x: usize, y: usize| f.height[y * 1024 + x];
        let covariance: f32 = (0..32)
            .flat_map(|by| (0..32).map(move |bx| (bx * 32, by * 32)))
            .map(|(x, y)| (at(&full, x, y) - 0.5) * (at(&limited, x, y) - 0.5))
            .sum::<f32>()
            / 1024.0;
        assert!(covariance > 0.0, "pooling structure survives");
    }

    #[test]
    fn simulation_paper_keeps_full_octaves() {
        // `generate_with_aspect` is the sim path and must be untouched: it is
        // identical to the band-limited generator with `pixel_scale = 0`.
        let paper = Paper::rough(Seed(7));
        let a = PaperField::generate_with_aspect(&paper, 64, 64, 1.0);
        let b = PaperField::generate_with_pixel_scale(&paper, 64, 64, 1.0, 0.0);
        assert_eq!(a, b);
    }
}
