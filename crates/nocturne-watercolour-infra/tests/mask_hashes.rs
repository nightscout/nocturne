//! Bit-identity snapshot of every `SetMask` in the catalogue. For each detail
//! level, every catalogue id on both grounds is built, every `SetMask` in it
//! is rasterised at the scene's own simulation resolution and aspect, and the
//! outputs are folded (FNV-1a over the `f32::to_bits` of every element) into
//! one hash per detail, asserted against literals captured from the original
//! implementation.

use nocturne_watercolour_core::domain::paint::rasterize_mask_aspect;
use nocturne_watercolour_core::domain::{Background, Operation, Palette, Seed};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DetailLevel};

fn fold(state: &mut u64, out: &[f32]) {
    for v in out {
        for byte in v.to_bits().to_le_bytes() {
            *state ^= byte as u64;
            *state = state.wrapping_mul(0x0000_0100_0000_01b3);
        }
    }
}

fn combined(detail: DetailLevel) -> u64 {
    let palette = Palette::moonlight();
    let mut state = 0xcbf2_9ce4_8422_2325u64;
    for &id in ArtworkCatalogue::ids() {
        for background in [Background::Transparent, Background::TransparentOnDark] {
            let scene = ArtworkCatalogue::by_id_for(id, Seed(7), &palette, 0.7, detail, background)
                .expect("catalogue id");
            let res = scene.sim_resolution.0;
            let aspect = scene.aspect();
            for ev in &scene.timeline.events {
                if let Operation::SetMask(m) = &ev.op {
                    fold(&mut state, &rasterize_mask_aspect(m, res, res, aspect));
                }
            }
        }
    }
    state
}

#[test]
fn catalogue_mask_hashes_match_the_snapshot() {
    let expected: [(&str, u64); 4] = [
        ("small", 0x15a10a9ce5dbc7f9),
        ("medium", 0xc21af120d2ad4845),
        ("large", 0x3562e4c2097d4f2b),
        ("extralarge", 0x76364a7572a1ca46),
    ];
    for detail in DetailLevel::ALL {
        let want = expected
            .iter()
            .find(|(name, _)| *name == detail.name())
            .unwrap()
            .1;
        assert_eq!(combined(detail), want, "detail {:?}", detail.name());
    }
}
