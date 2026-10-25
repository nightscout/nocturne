//! Pigments in the Kubelka-Munk sense: absorption `K` and scattering `S` per
//! RGB channel, plus the three Curtis et al. behaviour coefficients.

/// Linear RGB triple.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Rgb(pub [f32; 3]);

impl Rgb {
    pub const fn new(r: f32, g: f32, b: f32) -> Self {
        Rgb([r, g, b])
    }

    pub fn map(self, f: impl Fn(f32) -> f32) -> Rgb {
        Rgb([f(self.0[0]), f(self.0[1]), f(self.0[2])])
    }

    pub fn zip(self, other: Rgb, f: impl Fn(f32, f32) -> f32) -> Rgb {
        Rgb([
            f(self.0[0], other.0[0]),
            f(self.0[1], other.0[1]),
            f(self.0[2], other.0[2]),
        ])
    }

    pub fn mean(self) -> f32 {
        (self.0[0] + self.0[1] + self.0[2]) / 3.0
    }
}

/// Clamps applied by [`Pigment::from_reflectance`].
///
/// The Curtis derivation has singularities at `Rb = 0` (division), `Rw = 1`
/// (division in the arccoth argument) and when `Rb >= Rw` (a pigment cannot
/// reflect more over black than over white, and the arccoth argument drops
/// below 1). Inputs are clamped so `REFLECTANCE_MIN <= Rb <= Rw - REFLECTANCE_GAP`
/// and `Rw <= REFLECTANCE_MAX` before the formulas run.
pub const REFLECTANCE_MIN: f32 = 0.002;
pub const REFLECTANCE_MAX: f32 = 0.98;
pub const REFLECTANCE_GAP: f32 = 0.002;

#[derive(Debug, Clone, PartialEq)]
pub struct Pigment {
    pub name: String,
    /// Absorption per unit thickness, per channel.
    pub k: Rgb,
    /// Scattering per unit thickness, per channel.
    pub s: Rgb,
    /// Curtis rho: how readily pigment settles out of water onto the paper.
    pub density: f32,
    /// Curtis omega: resistance to being lifted back into water once deposited.
    pub staining_power: f32,
    /// Curtis gamma: how strongly paper height modulates deposition.
    pub granulation: f32,
}

impl Pigment {
    /// Derives `K` and `S` from measured reflectance over white (`r_white`)
    /// and over black (`r_black`), per channel, following Curtis et al.:
    ///
    /// ```text
    /// a = 1/2 (Rw + (Rb - Rw + 1) / Rb)
    /// b = sqrt(a^2 - 1)
    /// S = (1/b) arccoth((b^2 - (a - Rw)(a - 1)) / (b (1 - Rw)))
    /// K = S (a - 1)
    /// ```
    ///
    /// See [`REFLECTANCE_MIN`] for the clamps applied first.
    pub fn from_reflectance(
        name: impl Into<String>,
        r_white: Rgb,
        r_black: Rgb,
        density: f32,
        staining_power: f32,
        granulation: f32,
    ) -> Pigment {
        let mut k = [0.0f32; 3];
        let mut s = [0.0f32; 3];
        for c in 0..3 {
            let rw = r_white.0[c].clamp(REFLECTANCE_MIN + REFLECTANCE_GAP, REFLECTANCE_MAX);
            let rb = r_black.0[c].clamp(REFLECTANCE_MIN, rw - REFLECTANCE_GAP);
            let a = 0.5 * (rw + (rb - rw + 1.0) / rb);
            let b = (a * a - 1.0).max(1e-6).sqrt();
            let arg = ((b * b - (a - rw) * (a - 1.0)) / (b * (1.0 - rw))).max(1.0 + 1e-5);
            let sc = arccoth(arg) / b;
            s[c] = sc.max(1e-4);
            k[c] = (sc * (a - 1.0)).max(0.0);
        }
        Pigment {
            name: name.into(),
            k: Rgb(k),
            s: Rgb(s),
            density,
            staining_power,
            granulation,
        }
    }
}

impl Pigment {
    /// A version for artwork shown on a dark ground. Under the premultiplied
    /// conversion in `optics`, what glows over a dark ground is the light a
    /// glaze *transmits* in its hue channels, so the variant is made more
    /// transparent (`Rb` scaled down), paler and more chromatic (`Rw` pushed
    /// away from its mean and lifted toward 1), and thinner (`K`/`S` halved
    /// per unit of concentration). A scattering "body" version would read as
    /// grey paint on black instead. Behaviour coefficients are kept.
    pub fn luminous(&self) -> Pigment {
        let (rw, rb) = self.design_reflectance();
        let mean = rw.mean();
        let rw2 = rw.map(|c| (mean + (c - mean) * 1.3 + (1.0 - mean) * 0.25).clamp(0.05, 0.98));
        let rb2 = rb.map(|c| c * 0.3);
        let mut p = Pigment::from_reflectance(
            format!("{}_luminous", self.name),
            rw2,
            rb2,
            self.density,
            self.staining_power,
            self.granulation,
        );
        p.k = p.k.map(|k| k * 0.5);
        p.s = p.s.map(|s| s * 0.5);
        p
    }

    /// Reflectance over white and over black of a unit-thickness layer,
    /// the inverse of [`Pigment::from_reflectance`] up to its clamps.
    pub fn design_reflectance(&self) -> (Rgb, Rgb) {
        let mut rw = [0.0; 3];
        let mut rb = [0.0; 3];
        for c in 0..3 {
            let (r, t) = super::optics::layer(self.k.0[c], self.s.0[c]);
            rb[c] = r;
            rw[c] = r + t * t / (1.0 - r).max(1e-6);
        }
        (Rgb(rw), Rgb(rb))
    }
}

fn arccoth(x: f32) -> f32 {
    0.5 * ((x + 1.0) / (x - 1.0)).ln()
}

/// Built-in pigments. Reflectances are chosen for how the glaze reads, not
/// from a spectrophotometer; `r_black` near zero gives a transparent
/// staining pigment, larger gives body and opacity.
pub mod builtin {
    use super::{Pigment, Rgb};

    pub fn indigo() -> Pigment {
        Pigment::from_reflectance(
            "indigo",
            Rgb::new(0.16, 0.20, 0.36),
            Rgb::new(0.01, 0.015, 0.05),
            0.55,
            0.85,
            0.35,
        )
    }

    pub fn paynes_grey() -> Pigment {
        Pigment::from_reflectance(
            "paynes_grey",
            Rgb::new(0.22, 0.25, 0.32),
            Rgb::new(0.03, 0.035, 0.05),
            0.75,
            0.5,
            0.7,
        )
    }

    pub fn phthalo_blue() -> Pigment {
        Pigment::from_reflectance(
            "phthalo_blue",
            Rgb::new(0.08, 0.28, 0.62),
            Rgb::new(0.005, 0.02, 0.08),
            0.35,
            0.95,
            0.1,
        )
    }

    pub fn cerulean() -> Pigment {
        Pigment::from_reflectance(
            "cerulean",
            Rgb::new(0.30, 0.55, 0.78),
            Rgb::new(0.10, 0.22, 0.40),
            0.9,
            0.3,
            0.85,
        )
    }

    pub fn quinacridone_rose() -> Pigment {
        Pigment::from_reflectance(
            "quinacridone_rose",
            Rgb::new(0.72, 0.18, 0.36),
            Rgb::new(0.06, 0.005, 0.02),
            0.4,
            0.9,
            0.05,
        )
    }

    pub fn raw_sienna() -> Pigment {
        Pigment::from_reflectance(
            "raw_sienna",
            Rgb::new(0.66, 0.44, 0.18),
            Rgb::new(0.12, 0.07, 0.02),
            0.8,
            0.45,
            0.6,
        )
    }

    pub fn burnt_umber() -> Pigment {
        Pigment::from_reflectance(
            "burnt_umber",
            Rgb::new(0.30, 0.19, 0.12),
            Rgb::new(0.04, 0.025, 0.015),
            0.85,
            0.5,
            0.65,
        )
    }

    pub fn sap_green() -> Pigment {
        Pigment::from_reflectance(
            "sap_green",
            Rgb::new(0.28, 0.44, 0.14),
            Rgb::new(0.02, 0.05, 0.01),
            0.5,
            0.7,
            0.25,
        )
    }

    pub fn viridian() -> Pigment {
        Pigment::from_reflectance(
            "viridian",
            Rgb::new(0.12, 0.46, 0.40),
            Rgb::new(0.02, 0.09, 0.08),
            0.8,
            0.4,
            0.7,
        )
    }

    pub fn lamp_black() -> Pigment {
        Pigment::from_reflectance(
            "lamp_black",
            Rgb::new(0.14, 0.14, 0.15),
            Rgb::new(0.02, 0.02, 0.022),
            0.7,
            0.6,
            0.45,
        )
    }

    pub fn moon_gold() -> Pigment {
        Pigment::from_reflectance(
            "moon_gold",
            Rgb::new(0.96, 0.86, 0.58),
            Rgb::new(0.30, 0.24, 0.10),
            0.6,
            0.4,
            0.2,
        )
    }

    pub fn ember_orange() -> Pigment {
        Pigment::from_reflectance(
            "ember_orange",
            Rgb::new(0.90, 0.42, 0.12),
            Rgb::new(0.10, 0.03, 0.005),
            0.45,
            0.8,
            0.15,
        )
    }

    pub fn all() -> Vec<Pigment> {
        vec![
            indigo(),
            paynes_grey(),
            phthalo_blue(),
            cerulean(),
            quinacridone_rose(),
            raw_sienna(),
            burnt_umber(),
            sap_green(),
            viridian(),
            lamp_black(),
            moon_gold(),
            ember_orange(),
        ]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn derived_coefficients_are_finite_and_positive() {
        for p in builtin::all() {
            for c in 0..3 {
                assert!(p.k.0[c].is_finite() && p.k.0[c] >= 0.0, "{} K", p.name);
                assert!(p.s.0[c].is_finite() && p.s.0[c] > 0.0, "{} S", p.name);
            }
        }
    }

    #[test]
    fn luminous_variant_is_thinner_more_transparent_and_still_glows() {
        use super::super::optics::{layer_rgb, to_premultiplied};
        for p in builtin::all() {
            let lum = p.luminous();
            let (_, rb) = p.design_reflectance();
            let (_, rb_lum) = lum.design_reflectance();
            assert!(rb_lum.mean() < rb.mean() + 1e-3, "{} transparency", p.name);
            let (r, t) = layer_rgb(p.k, p.s, 0.5);
            let (r2, t2) = layer_rgb(lum.k, lum.s, 0.5);
            let px = to_premultiplied(r, t);
            let px2 = to_premultiplied(r2, t2);
            assert!(px2[3] < px[3], "{} alpha {} vs {}", p.name, px2[3], px[3]);
            // Glow is transmitted hue light, so a neutral grey has none of it
            // by construction; only chromatic pigments are checked for glow.
            let neutral = matches!(p.name.as_str(), "paynes_grey" | "lamp_black");
            if !neutral {
                let glow = px2[0].max(px2[1]).max(px2[2]);
                let chroma = glow - px2[0].min(px2[1]).min(px2[2]);
                assert!(glow > 0.08, "{} glow {glow}", p.name);
                assert!(chroma > 0.05, "{} chroma {chroma}", p.name);
            }
        }
    }

    #[test]
    fn design_reflectance_inverts_from_reflectance() {
        let p = builtin::quinacridone_rose();
        let (rw, rb) = p.design_reflectance();
        for (c, expect) in rw.0.iter().zip([0.72, 0.18, 0.36]) {
            assert!((c - expect).abs() < 0.02, "{c} vs {expect}");
        }
        for (c, expect) in rb.0.iter().zip([0.06, 0.005, 0.02]) {
            assert!((c - expect).abs() < 0.02, "{c} vs {expect}");
        }
    }

    #[test]
    fn degenerate_inputs_are_clamped_not_nan() {
        let p = Pigment::from_reflectance(
            "degenerate",
            Rgb::new(1.0, 0.0, 0.5),
            Rgb::new(1.0, 0.0, 0.9),
            1.0,
            1.0,
            1.0,
        );
        for c in 0..3 {
            assert!(p.k.0[c].is_finite());
            assert!(p.s.0[c].is_finite());
        }
    }
}
