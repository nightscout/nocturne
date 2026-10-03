//! Times `rasterize_mask_aspect` so the segment-culling work can be measured
//! before and after:
//!
//! ```text
//! cargo run -p nocturne-watercolour-core --example mask_raster_timing --release
//! ```
//!
//! For each outline (16-, 96- and 400-point circles), each mask variant
//! (`Polygon` and `Path`), each grid side (96, 256, 384) and each aspect
//! (1.0, 2.0) it prints the median wall-clock of five rasterisations.

use std::time::Instant;

use nocturne_watercolour_core::domain::paint::rasterize_mask_aspect;
use nocturne_watercolour_core::domain::{Mask, Point};

fn circle(centre: (f32, f32), radius: f32, samples: usize) -> Vec<Point> {
    (0..samples)
        .map(|i| {
            let a = i as f32 / samples as f32 * std::f32::consts::TAU;
            Point::new(centre.0 + radius * a.cos(), centre.1 + radius * a.sin())
        })
        .collect()
}

fn median(mut runs: Vec<f64>) -> f64 {
    runs.sort_by(|a, b| a.total_cmp(b));
    runs[runs.len() / 2]
}

fn main() {
    const RUNS: usize = 5;
    const SIDES: [u32; 3] = [96, 256, 384];
    const ASPECTS: [f32; 2] = [1.0, 2.0];
    const N_POINTS: [usize; 3] = [16, 96, 400];
    println!(
        "{:>6} {:>8} {:>5} {:>7} {:>10}",
        "points", "variant", "grid", "aspect", "median_ms"
    );
    for &n in &N_POINTS {
        let outline = circle((0.5, 0.5), 0.35, n);
        let masks = [
            (
                "polygon",
                Mask::Polygon {
                    points: outline.clone(),
                    feather: 0.02,
                },
            ),
            (
                "path",
                Mask::Path {
                    points: outline.clone(),
                    radius: 0.03,
                    feather: 0.02,
                },
            ),
        ];
        for (variant, mask) in &masks {
            for &side in &SIDES {
                for &aspect in &ASPECTS {
                    let mut runs = Vec::with_capacity(RUNS);
                    for _ in 0..RUNS {
                        let t = Instant::now();
                        let out = rasterize_mask_aspect(mask, side, side, aspect);
                        runs.push(t.elapsed().as_secs_f64() * 1000.0);
                        std::hint::black_box(out);
                    }
                    println!(
                        "{n:>6} {variant:>8} {side:>5} {aspect:>7} {:>10.3}",
                        median(runs),
                    );
                }
            }
        }
    }
}
