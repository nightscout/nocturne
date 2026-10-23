//! Bit-identity of every `SetMask` in the catalogue against the original,
//! unaccelerated rasteriser, kept below as `reference_rasterize`. For each
//! detail level, every catalogue id on both grounds is built and every
//! `SetMask` in it is rasterised at the scene's own simulation resolution and
//! aspect by both.
//!
//! The comparison is against a reference rather than stored hashes because the
//! catalogue authors its outlines with `f32::sin` and `f32::cos`, which call
//! the platform libm: glibc and the MSVC CRT round some angles to neighbouring
//! ulps, so the masks, and any hash of them, differ between platforms.
//! `nocturne-watercolour-core/tests/mask_raster.rs` holds portable hashes.

use nocturne_watercolour_core::domain::paint::rasterize_mask_aspect;
use nocturne_watercolour_core::domain::scene::isotropic_scale;
use nocturne_watercolour_core::domain::{Background, Mask, Operation, Palette, Point, Seed};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DetailLevel};

fn distance_to_segment(px: f32, py: f32, a: Point, b: Point) -> f32 {
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
    ((px - cx).powi(2) + (py - cy).powi(2)).sqrt()
}

fn polyline_distance(px: f32, py: f32, points: &[Point], closed: bool) -> f32 {
    if points.is_empty() {
        return f32::MAX;
    }
    if points.len() == 1 {
        return distance_to_segment(px, py, points[0], points[0]);
    }
    let mut best = f32::MAX;
    for pair in points.windows(2) {
        best = best.min(distance_to_segment(px, py, pair[0], pair[1]));
    }
    if closed {
        best = best.min(distance_to_segment(
            px,
            py,
            points[points.len() - 1],
            points[0],
        ));
    }
    best
}

fn point_in_polygon(px: f32, py: f32, points: &[Point]) -> bool {
    let mut inside = false;
    let n = points.len();
    if n < 3 {
        return false;
    }
    let mut j = n - 1;
    for i in 0..n {
        let (a, b) = (points[i], points[j]);
        if (a.y > py) != (b.y > py) {
            let x_at = a.x + (py - a.y) / (b.y - a.y) * (b.x - a.x);
            if px < x_at {
                inside = !inside;
            }
        }
        j = i;
    }
    inside
}

/// Every cell against every segment, as `rasterize_mask_aspect` did before it
/// was bucketed and banded.
fn reference_rasterize(mask: &Mask, width: u32, height: u32, aspect: f32) -> Vec<f32> {
    let mut out = vec![0.0f32; (width as usize) * (height as usize)];
    let (w, h) = (width as f32, height as f32);
    let (ax, ay) = isotropic_scale(aspect);
    let iso_points: Vec<Point> = mask
        .points()
        .iter()
        .map(|p| Point::new(p.x * ax, p.y * ay))
        .collect();
    let feather = mask.feather();
    for y in 0..height {
        for x in 0..width {
            let pu = (x as f32 + 0.5) / w;
            let pv = (y as f32 + 0.5) / h;
            let (iu, iv) = (pu * ax, pv * ay);
            let outside = match mask {
                Mask::Polygon { points, .. } => {
                    if point_in_polygon(pu, pv, points) {
                        0.0
                    } else {
                        polyline_distance(iu, iv, &iso_points, true)
                    }
                }
                Mask::Path { radius, .. } => {
                    (polyline_distance(iu, iv, &iso_points, false) - radius).max(0.0)
                }
            };
            out[(y as usize) * (width as usize) + x as usize] = if outside <= 0.0 {
                1.0
            } else if feather <= 1e-6 {
                0.0
            } else {
                let t = (outside / feather).min(1.0);
                1.0 - t * t * (3.0 - 2.0 * t)
            };
        }
    }
    out
}

#[test]
fn catalogue_masks_match_the_reference_rasteriser() {
    let palette = Palette::moonlight();
    let mut compared = 0;
    for detail in DetailLevel::ALL {
        for &id in ArtworkCatalogue::ids() {
            for background in [Background::Transparent, Background::TransparentOnDark] {
                let scene =
                    ArtworkCatalogue::by_id_for(id, Seed(7), &palette, 0.7, detail, background)
                        .expect("catalogue id");
                let res = scene.sim_resolution.0;
                let aspect = scene.aspect();
                for ev in &scene.timeline.events {
                    if let Operation::SetMask(m) = &ev.op {
                        let got: Vec<u32> = rasterize_mask_aspect(m, res, res, aspect)
                            .iter()
                            .map(|v| v.to_bits())
                            .collect();
                        let want: Vec<u32> = reference_rasterize(m, res, res, aspect)
                            .iter()
                            .map(|v| v.to_bits())
                            .collect();
                        assert!(
                            got == want,
                            "{id} ({:?}, {background:?}) mask differs from the reference",
                            detail.name()
                        );
                        compared += 1;
                    }
                }
            }
        }
    }
    assert!(compared > 0, "the catalogue has no SetMask to compare");
}
