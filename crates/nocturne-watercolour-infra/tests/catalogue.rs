//! The catalogue contract: every id builds a valid scene for every palette,
//! detail level and intensity extreme, and each renders to something.

use std::collections::HashSet;

use nocturne_watercolour_core::application::{CpuEngine, Playback, Renderer};
use nocturne_watercolour_core::domain::{Background, Palette, Seed, SimResolution};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DetailLevel};

const EXPECTED_IDS: [&str; 38] = [
    "crescent-moon",
    "alarm-bell",
    "linked-rings",
    "report-pages",
    "magnifying-glass",
    "confirmation-mark",
    "moonlit-shoreline",
    "distant-mountains",
    "connected-shores",
    "overlapping-shapes",
    "avatar-wash",
    "tab-underline",
    "selection-edge",
    "confirmation-background",
    "header-motif",
    "calendar",
    "clock",
    "stopwatch",
    "sunrise",
    "footprints",
    "apple",
    "pizza-slice",
    "spanner",
    "suitcase",
    "paint-palette",
    "key",
    "plug",
    "apartment",
    "world-globe",
    "github-mark",
    "heart",
    "blood-drop",
    "heart-rate",
    "shield",
    "people-group",
    "exclamation-mark",
    "chat-bubble",
    "phone",
];

fn palettes() -> Vec<Palette> {
    Palette::NAMES
        .iter()
        .flat_map(|name| {
            let p = Palette::by_name(name).unwrap();
            [p.for_dark_surface(), p]
        })
        .collect()
}

#[test]
fn ids_are_unique_and_match_the_web_union() {
    let ids = ArtworkCatalogue::ids();
    assert_eq!(ids, ArtworkCatalogue::IDS);
    assert_eq!(ids, &EXPECTED_IDS);
    let unique: HashSet<&str> = ids.iter().copied().collect();
    assert_eq!(unique.len(), ids.len());
}

#[test]
fn unknown_id_is_none() {
    let p = Palette::moonlight();
    assert!(
        ArtworkCatalogue::by_id("crescent_moon", Seed(1), &p, 0.7, DetailLevel::Large).is_none()
    );
    assert!(ArtworkCatalogue::by_id("", Seed(1), &p, 0.7, DetailLevel::Large).is_none());
}

#[test]
fn every_id_validates_for_every_palette_detail_and_intensity() {
    for id in ArtworkCatalogue::ids() {
        for palette in palettes() {
            for detail in DetailLevel::ALL {
                for intensity in [0.3, 1.0] {
                    let scene = ArtworkCatalogue::by_id(id, Seed(7), &palette, intensity, detail)
                        .unwrap_or_else(|| panic!("{id} missing"));
                    assert_eq!(
                        scene.validate(),
                        Ok(()),
                        "{id} / {} / {detail:?} / {intensity}",
                        palette.name
                    );
                    let res = scene.sim_resolution.0;
                    assert!(
                        (SimResolution::MIN..=SimResolution::MAX).contains(&res),
                        "{id} sim resolution {res}"
                    );
                    assert_eq!(res, detail.sim_resolution());
                    assert_eq!(scene.palette, palette);
                    assert_eq!(scene.seed, Seed(7));
                }
            }
        }
    }
}

#[test]
fn small_is_simpler_and_cheaper_than_large() {
    let palette = Palette::dusk();
    for id in ArtworkCatalogue::ids() {
        let small =
            ArtworkCatalogue::by_id(id, Seed(3), &palette, 0.7, DetailLevel::Small).unwrap();
        let medium =
            ArtworkCatalogue::by_id(id, Seed(3), &palette, 0.7, DetailLevel::Medium).unwrap();
        let large =
            ArtworkCatalogue::by_id(id, Seed(3), &palette, 0.7, DetailLevel::Large).unwrap();
        assert!(
            small.timeline.events.len() < large.timeline.events.len(),
            "{id}: small {} events vs large {}",
            small.timeline.events.len(),
            large.timeline.events.len()
        );
        assert!(
            small.timeline.events.len() <= medium.timeline.events.len(),
            "{id}"
        );
        assert!(
            medium.timeline.events.len() <= large.timeline.events.len(),
            "{id}"
        );
        assert!(
            small.timeline.total_ticks < large.timeline.total_ticks,
            "{id}"
        );
        assert!(small.sim_resolution.0 < large.sim_resolution.0, "{id}");
        assert_eq!(small.size_hint, large.size_hint, "{id}");
    }
}

#[test]
fn same_inputs_build_identical_scenes_and_different_seeds_differ() {
    let palette = Palette::moss();
    for id in ArtworkCatalogue::ids() {
        let a = ArtworkCatalogue::by_id(id, Seed(11), &palette, 0.7, DetailLevel::Medium).unwrap();
        let b = ArtworkCatalogue::by_id(id, Seed(11), &palette, 0.7, DetailLevel::Medium).unwrap();
        let c = ArtworkCatalogue::by_id(id, Seed(12), &palette, 0.7, DetailLevel::Medium).unwrap();
        assert_eq!(a, b, "{id}");
        assert_ne!(a, c, "{id}");
    }
}

#[test]
fn intensity_changes_pigment_amounts_only() {
    let palette = Palette::water();
    for id in ArtworkCatalogue::ids() {
        let lo = ArtworkCatalogue::by_id(id, Seed(2), &palette, 0.3, DetailLevel::Large).unwrap();
        let hi = ArtworkCatalogue::by_id(id, Seed(2), &palette, 1.0, DetailLevel::Large).unwrap();
        assert_eq!(lo.timeline.events.len(), hi.timeline.events.len(), "{id}");
        assert_ne!(lo, hi, "{id}");
    }
}

#[test]
fn every_id_renders_something_finite_on_the_cpu_at_small() {
    for id in ArtworkCatalogue::ids() {
        let scene =
            ArtworkCatalogue::by_id(id, Seed(5), &Palette::moonlight(), 0.7, DetailLevel::Small)
                .unwrap();
        let mut pb = Playback::new(CpuEngine::default(), scene.clone(), 1000.0).unwrap();
        pb.finish_immediately().unwrap();
        // Half the size-hint, so a thin hairline (tab-underline is a sub-pixel
        // mark at /8) still lands on a pixel instead of aliasing away.
        let (w, h) = (scene.size_hint.width / 4, scene.size_hint.height / 4);
        let image = pb.simulator().render(w.max(8), h.max(8)).unwrap();
        assert!(image.rgba.iter().all(|v| v.is_finite()), "{id} has NaN");
        let alpha_max = image.rgba.chunks(4).map(|p| p[3]).fold(0.0f32, f32::max);
        assert!(
            alpha_max > 0.05,
            "{id} rendered nothing (max alpha {alpha_max})"
        );
        assert!(alpha_max <= 1.0, "{id} alpha {alpha_max}");
    }
}

#[test]
fn dark_ground_scenes_validate_carry_the_background_and_drop_the_greying_marks() {
    for id in ArtworkCatalogue::ids() {
        for palette in palettes() {
            for detail in DetailLevel::ALL {
                let dark = ArtworkCatalogue::by_id_for(
                    id,
                    Seed(4),
                    &palette,
                    0.7,
                    detail,
                    Background::TransparentOnDark,
                )
                .unwrap();
                assert_eq!(
                    dark.validate(),
                    Ok(()),
                    "{id} / {} / {detail:?}",
                    palette.name
                );
                assert_eq!(dark.background, Background::TransparentOnDark, "{id}");
                let light = ArtworkCatalogue::by_id(id, Seed(4), &palette, 0.7, detail).unwrap();
                assert_eq!(light.background, Background::Transparent, "{id}");
                assert!(
                    dark.timeline.events.len() <= light.timeline.events.len(),
                    "{id}: dark ground must not add marks"
                );
            }
        }
    }
    let light = ArtworkCatalogue::by_id(
        "crescent-moon",
        Seed(4),
        &Palette::moonlight(),
        0.7,
        DetailLevel::Large,
    )
    .unwrap();
    let dark = ArtworkCatalogue::by_id_for(
        "crescent-moon",
        Seed(4),
        &Palette::moonlight(),
        0.7,
        DetailLevel::Large,
        Background::TransparentOnDark,
    )
    .unwrap();
    assert!(dark.timeline.events.len() < light.timeline.events.len());
}
