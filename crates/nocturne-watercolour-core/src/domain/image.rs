//! Rendered output: premultiplied-alpha linear RGBA in `f32`.

use super::pigment::Rgb;

#[derive(Debug, Clone, PartialEq)]
pub struct Image {
    pub width: u32,
    pub height: u32,
    /// `width * height * 4` premultiplied linear values.
    pub rgba: Vec<f32>,
}

impl Image {
    pub fn new(width: u32, height: u32) -> Image {
        Image {
            width,
            height,
            rgba: vec![0.0; (width as usize) * (height as usize) * 4],
        }
    }

    pub fn pixel(&self, x: u32, y: u32) -> [f32; 4] {
        let i = ((y as usize) * (self.width as usize) + x as usize) * 4;
        [
            self.rgba[i],
            self.rgba[i + 1],
            self.rgba[i + 2],
            self.rgba[i + 3],
        ]
    }

    /// Standard "over" onto an opaque linear background; result is opaque.
    pub fn composite_over(&self, background: Rgb) -> Image {
        let mut out = self.clone();
        for px in out.rgba.chunks_exact_mut(4) {
            let a = px[3].clamp(0.0, 1.0);
            for (p, b) in px.iter_mut().zip(background.0) {
                *p += b * (1.0 - a);
            }
            px[3] = 1.0;
        }
        out
    }

    /// Mean absolute difference over all channels; `None` if sizes differ.
    pub fn mean_abs_diff(&self, other: &Image) -> Option<f32> {
        if self.width != other.width || self.height != other.height {
            return None;
        }
        let sum: f64 = self
            .rgba
            .iter()
            .zip(&other.rgba)
            .map(|(a, b)| f64::from((a - b).abs()))
            .sum();
        Some((sum / self.rgba.len().max(1) as f64) as f32)
    }
}
