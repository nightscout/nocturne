//! The Lucide test-set contract: every icon in `tests/fixtures/lucide-icons.json`
//! builds a valid scene for every detail level and both grounds, with the
//! per-icon hints the web library ships applied.

use nocturne_watercolour_core::domain::{Background, Operation, Palette, Seed};
use nocturne_watercolour_infra::authoring::{
    DetailLevel, IconHints, parse_icon_elements, parse_icon_hints, svg_icon_scene,
};

const ICON_JSON: &str = concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/tests/fixtures/lucide-icons.json"
);

const TEST_SET: [&str; 18] = [
    "clock",
    "calendar",
    "heart",
    "key",
    "phone",
    "bell",
    "database",
    "fingerprint",
    "server",
    "syringe",
    "sprout",
    "scale",
    "battery",
    "flag",
    "book-open",
    "megaphone",
    "rocket",
    "cpu",
];

/// The library's built-in tuning (`src/api/icon-hints.ts`), mirrored so the
/// Rust side exercises the same hints the browser ships.
const TEST_HINTS_JSON: &str = r#"{
  "database": { "fill": [1] },
  "flag": { "fill": [0] },
  "sprout": { "fill": [0] },
  "key": { "holes": [[7.5, 15.5, 2.2]] },
  "clock": { "markRadius": 1.4 },
  "cpu": { "markRadius": 1.5, "smallMarks": 4 },
  "fingerprint": { "markRadius": 1.3, "smallMarks": 3 },
  "syringe": { "markRadius": 1.3, "smallMarks": 2 },
  "battery": { "markRadius": 1.6 }
}"#;

fn hints(name: &str) -> IconHints {
    let table: serde_json::Map<String, serde_json::Value> =
        serde_json::from_str(TEST_HINTS_JSON).expect("hints table");
    table
        .get(name)
        .map(|v| parse_icon_hints(&v.to_string()).expect("hints parse"))
        .unwrap_or_default()
}

fn elements(name: &str) -> Vec<nocturne_watercolour_infra::authoring::IconNode> {
    let json = std::fs::read_to_string(ICON_JSON).expect("tests/fixtures/lucide-icons.json");
    let data: serde_json::Map<String, serde_json::Value> =
        serde_json::from_str(&json).expect("icon map");
    let list = data
        .get(name)
        .unwrap_or_else(|| panic!("{name} missing from icon map"))
        .to_string();
    parse_icon_elements(&list).unwrap_or_else(|e| panic!("{name}: {e}"))
}

#[test]
fn every_icon_builds_a_valid_scene_for_every_detail_and_ground() {
    let palette = Palette::moonlight();
    for &name in &TEST_SET {
        let elements = elements(name);
        let hints = hints(name);
        for detail in DetailLevel::ALL {
            for background in [Background::Transparent, Background::TransparentOnDark] {
                let scene = svg_icon_scene(
                    name,
                    &elements,
                    Seed(7),
                    &palette,
                    0.7,
                    detail,
                    background,
                    None,
                    &hints,
                );
                assert_eq!(
                    scene.validate(),
                    Ok(()),
                    "{name} / {detail:?} / {background:?}"
                );
                assert_eq!(scene.background, background, "{name}");
                assert!(
                    scene.id.0.starts_with(&format!("lucide-{name}-")),
                    "{name} id: {}",
                    scene.id.0
                );
                assert_eq!(scene.sim_resolution.0, detail.sim_resolution(), "{name}");
            }
        }
    }
}

#[test]
fn small_is_simpler_than_large() {
    let palette = Palette::dusk();
    for &name in &TEST_SET {
        let elements = elements(name);
        let hints = hints(name);
        let small = svg_icon_scene(
            name,
            &elements,
            Seed(3),
            &palette,
            0.7,
            DetailLevel::Small,
            Background::Transparent,
            None,
            &hints,
        );
        let large = svg_icon_scene(
            name,
            &elements,
            Seed(3),
            &palette,
            0.7,
            DetailLevel::Large,
            Background::Transparent,
            None,
            &hints,
        );
        assert!(
            small.timeline.events.len() <= large.timeline.events.len(),
            "{name}: small {} events vs large {}",
            small.timeline.events.len(),
            large.timeline.events.len()
        );
        assert!(
            small.timeline.total_ticks < large.timeline.total_ticks,
            "{name}"
        );
    }
}

#[test]
fn same_inputs_build_identical_scenes() {
    let palette = Palette::moss();
    for &name in &TEST_SET {
        let elements = elements(name);
        let hints = hints(name);
        let a = svg_icon_scene(
            name,
            &elements,
            Seed(11),
            &palette,
            0.7,
            DetailLevel::Medium,
            Background::Transparent,
            None,
            &hints,
        );
        let b = svg_icon_scene(
            name,
            &elements,
            Seed(11),
            &palette,
            0.7,
            DetailLevel::Medium,
            Background::Transparent,
            None,
            &hints,
        );
        assert_eq!(a, b, "{name}");
    }
}

#[test]
fn fill_moves_exactly_the_named_subpaths_to_bodies() {
    let palette = Palette::moonlight();
    for (name, fill, bodies) in [
        // database: ellipse (closed) + cylinder sides (open) + mid ring (open).
        ("database", vec![1usize], 2usize),
        // flag: pole + pennant drawn as one open path.
        ("flag", vec![0usize], 1usize),
        // sprout: right leaf loop (open) + left leaf (closed) + ground line (open).
        ("sprout", vec![0usize], 2usize),
    ] {
        let elements = elements(name);
        let plain = svg_icon_scene(
            name,
            &elements,
            Seed(7),
            &palette,
            0.7,
            DetailLevel::Large,
            Background::Transparent,
            None,
            &IconHints::default(),
        );
        let hinted = svg_icon_scene(
            name,
            &elements,
            Seed(7),
            &palette,
            0.7,
            DetailLevel::Large,
            Background::Transparent,
            None,
            &IconHints {
                fill: fill.clone(),
                ..Default::default()
            },
        );
        let set_masks = |s: &nocturne_watercolour_core::domain::Scene| {
            s.timeline
                .events
                .iter()
                .filter(|e| matches!(&e.op, Operation::SetMask(_)))
                .count()
        };
        assert_eq!(set_masks(&hinted), bodies, "{name} bodies");
        assert_eq!(
            set_masks(&hinted),
            set_masks(&plain) + fill.len(),
            "{name}: each named open subpath becomes a body"
        );
    }
}
