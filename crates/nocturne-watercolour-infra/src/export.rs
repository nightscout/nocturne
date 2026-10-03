//! PNG export and reveal frame sequences.

use nocturne_watercolour_core::application::{
    EngineError, Exporter, Playback, Renderer, Simulator,
};
use nocturne_watercolour_core::domain::Image;

/// Encodes to 8-bit sRGB RGBA PNG. PNG stores straight (non-premultiplied)
/// alpha, so colour is divided back out of alpha before encoding.
#[derive(Debug, Default, Clone, Copy)]
pub struct PngExporter;

/// Below this alpha a pixel is written fully transparent black; dividing by
/// it would only amplify quantisation noise into visible colour fringes.
const MIN_ALPHA: f32 = 1.0 / 1024.0;

pub fn linear_to_srgb(v: f32) -> f32 {
    let v = v.clamp(0.0, 1.0);
    if v <= 0.003_130_8 {
        v * 12.92
    } else {
        1.055 * v.powf(1.0 / 2.4) - 0.055
    }
}

pub fn srgb_to_linear(v: f32) -> f32 {
    let v = v.clamp(0.0, 1.0);
    if v <= 0.040_45 {
        v / 12.92
    } else {
        ((v + 0.055) / 1.055).powf(2.4)
    }
}

/// Premultiplied linear RGBA to straight sRGB 8-bit RGBA.
pub fn to_srgb8_straight(image: &Image) -> Vec<u8> {
    let mut out = Vec::with_capacity(image.rgba.len());
    for px in image.rgba.chunks_exact(4) {
        let a = px[3].clamp(0.0, 1.0);
        if a < MIN_ALPHA {
            out.extend_from_slice(&[0, 0, 0, 0]);
            continue;
        }
        for &channel in &px[..3] {
            let straight = (channel / a).clamp(0.0, 1.0);
            out.push((linear_to_srgb(straight) * 255.0 + 0.5) as u8);
        }
        out.push((a * 255.0 + 0.5) as u8);
    }
    out
}

impl Exporter for PngExporter {
    fn encode(&self, image: &Image) -> Result<Vec<u8>, EngineError> {
        let data = to_srgb8_straight(image);
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, image.width, image.height);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder.set_source_srgb(png::SrgbRenderingIntent::Perceptual);
            let mut writer = encoder
                .write_header()
                .map_err(|e| EngineError::new(format!("png header: {e}")))?;
            writer
                .write_image_data(&data)
                .map_err(|e| EngineError::new(format!("png data: {e}")))?;
        }
        Ok(bytes)
    }
}

/// Renders `count` frames evenly spaced in artistic progress (`0..=1`), each
/// reached by seeking so the sequence is independent of how the playback
/// was previously driven. The last frame is the finished, fully dry artwork.
pub struct FrameSequence {
    pub count: u32,
    pub width: u32,
    pub height: u32,
}

impl FrameSequence {
    pub fn render<E: Simulator + Renderer>(
        &self,
        playback: &mut Playback<E>,
    ) -> Result<Vec<Image>, EngineError> {
        let count = self.count.max(1);
        let mut frames = Vec::with_capacity(count as usize);
        for i in 0..count {
            let progress = if count == 1 {
                1.0
            } else {
                i as f32 / (count - 1) as f32
            };
            if progress >= 1.0 {
                playback.finish_immediately()?;
            } else {
                playback.seek_progress(progress)?;
            }
            frames.push(playback.simulator().render(self.width, self.height)?);
        }
        Ok(frames)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unpremultiply_recovers_straight_colour_and_handles_zero_alpha() {
        let mut img = Image::new(2, 1);
        img.rgba[0..4].copy_from_slice(&[0.25, 0.0, 0.125, 0.5]);
        img.rgba[4..8].copy_from_slice(&[0.3, 0.3, 0.3, 0.0]);
        let bytes = to_srgb8_straight(&img);
        assert_eq!(bytes[0], (linear_to_srgb(0.5) * 255.0 + 0.5) as u8);
        assert_eq!(bytes[1], 0);
        assert_eq!(bytes[2], (linear_to_srgb(0.25) * 255.0 + 0.5) as u8);
        assert_eq!(bytes[3], 128);
        assert_eq!(&bytes[4..8], &[0, 0, 0, 0]);
    }

    #[test]
    fn srgb_roundtrip() {
        for i in 0..=20 {
            let v = i as f32 / 20.0;
            assert!((srgb_to_linear(linear_to_srgb(v)) - v).abs() < 1e-5);
        }
    }

    #[test]
    fn encodes_a_valid_png() {
        let img = Image::new(4, 3);
        let bytes = PngExporter.encode(&img).unwrap();
        assert_eq!(
            &bytes[..8],
            &[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A]
        );
        let decoder = png::Decoder::new(std::io::Cursor::new(bytes));
        let mut reader = decoder.read_info().unwrap();
        let mut buf = vec![0; reader.output_buffer_size().unwrap()];
        let info = reader.next_frame(&mut buf).unwrap();
        assert_eq!((info.width, info.height), (4, 3));
    }
}
