//! Times every `SetMask` rasterisation in the whole catalogue at the two
//! largest detail levels, summed per scene and overall, so the mask work can
//! be measured before and after the segment-culling change:
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example catalogue_mask_timing --release
//! ```
//!
//! Each scene is built with `ArtworkCatalogue::by_id_for` and rasterised at
//! its own simulation resolution and aspect, the way the simulator does.

use std::time::Instant;

use nocturne_watercolour_core::domain::paint::rasterize_mask_aspect;
use nocturne_watercolour_core::domain::{Background, Operation, Palette, Seed};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DetailLevel};

fn main() {
    let palette = Palette::moonlight();
    for detail in [DetailLevel::Large, DetailLevel::ExtraLarge] {
        let mut overall = 0.0;
        let mut overall_masks = 0usize;
        println!("== {detail:?}");
        println!("{:>20} {:>6} {:>10}", "id", "masks", "sum_ms");
        for &id in ArtworkCatalogue::ids() {
            let scene = ArtworkCatalogue::by_id_for(
                id,
                Seed(7),
                &palette,
                0.7,
                detail,
                Background::Transparent,
            )
            .expect("catalogue id");
            let res = scene.sim_resolution.0;
            let aspect = scene.aspect();
            let mut sum = 0.0;
            let mut masks = 0usize;
            for ev in &scene.timeline.events {
                if let Operation::SetMask(m) = &ev.op {
                    masks += 1;
                    let t = Instant::now();
                    let out = rasterize_mask_aspect(m, res, res, aspect);
                    sum += t.elapsed().as_secs_f64() * 1000.0;
                    std::hint::black_box(out);
                }
            }
            overall += sum;
            overall_masks += masks;
            println!("{id:>20} {masks:>6} {sum:>10.3}");
        }
        println!("overall {overall:.3} ms across {overall_masks} masks");
    }
}
