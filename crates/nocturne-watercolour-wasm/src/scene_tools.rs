//! Scene construction and export helpers shared by the browser bindings and
//! the native bake example.

use nocturne_watercolour_core::domain::scene::MAX_CONCENTRATION;
use nocturne_watercolour_core::domain::{
    Background, Image, Operation, Palette, PaletteEntry, Pigment, PigmentRole, Rgb, Scene, Seed,
    SimResolution,
};
use nocturne_watercolour_infra::authoring::ArtworkCatalogue;
pub use nocturne_watercolour_infra::authoring::DetailLevel;
use nocturne_watercolour_infra::authoring::{
    parse_icon_elements, parse_icon_hints, svg_icon_scene,
};
use nocturne_watercolour_infra::document::{PaletteDoc, scene_to_json};
use serde::Serialize;

/// Intensity the catalogue scenes are authored at; the baked assets use it.
pub const DEFAULT_INTENSITY: f32 = 0.7;

/// Fraction of the ticks the final settle phase covers when a caller asks for
/// a tail (0 leaves the authored timeline untouched).
pub const DEFAULT_SETTLE_FRACTION: f32 = 0.3;

/// [`nocturne_watercolour_core::application::apply_settle_fraction`] over the
/// scene's timeline, so the browser bindings keep addressing it through the
/// scene rather than its timeline.
pub fn apply_settle_fraction(scene: &mut Scene, fraction: f32) {
    nocturne_watercolour_core::application::apply_settle_fraction(&mut scene.timeline, fraction);
}

/// Frame-strip limits mirrored by the TypeScript manifest validator.
pub const MAX_STRIP_FRAMES: u32 = 16;
pub const MAX_STRIP_FRAME_EDGE: u32 = 256;
pub const BAKED_MANIFEST_VERSION: u32 = 1;

/// Every message is `Code: detail`; the TypeScript layer keys on the code.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SceneToolError {
    UnknownArtwork(String),
    UnknownPalette(String),
    InvalidPalette(String),
    InvalidScene(String),
    InvalidStrip(String),
    InvalidIcon(String),
}

impl std::fmt::Display for SceneToolError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SceneToolError::UnknownArtwork(id) => write!(f, "UnknownArtwork: {id}"),
            SceneToolError::UnknownPalette(p) => write!(f, "UnknownPalette: {p}"),
            SceneToolError::InvalidPalette(m) => write!(f, "InvalidPalette: {m}"),
            SceneToolError::InvalidScene(m) => write!(f, "InvalidScene: {m}"),
            SceneToolError::InvalidStrip(m) => write!(f, "InvalidStrip: {m}"),
            SceneToolError::InvalidIcon(m) => write!(f, "InvalidIcon: {m}"),
        }
    }
}

impl std::error::Error for SceneToolError {}

pub fn parse_detail(name: &str) -> Option<DetailLevel> {
    match name.trim().to_ascii_lowercase().as_str() {
        "small" => Some(DetailLevel::Small),
        "medium" => Some(DetailLevel::Medium),
        "large" | "" => Some(DetailLevel::Large),
        "extralarge" | "extra_large" => Some(DetailLevel::ExtraLarge),
        _ => None,
    }
}

/// The TypeScript side picks from the canvas's DPR-scaled backing long edge
/// with the same thresholds (`detailForEdge`).
pub fn detail_for_edge_px(edge: u32) -> DetailLevel {
    if edge < 64 {
        DetailLevel::Small
    } else if edge < 192 {
        DetailLevel::Medium
    } else if edge < 320 {
        DetailLevel::Large
    } else {
        DetailLevel::ExtraLarge
    }
}

/// The page background an artwork will sit on. `Dark` keeps the palette
/// and switches the scene to `Background::TransparentOnDark`, the luminous
/// compositing mode; the `<palette>_dark` palettes are for subtractive
/// compositing only and are not what this selects.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Surface {
    #[default]
    Light,
    Dark,
}

impl Surface {
    pub fn parse(name: &str) -> Option<Surface> {
        match name.trim().to_ascii_lowercase().as_str() {
            "light" | "" => Some(Surface::Light),
            "dark" => Some(Surface::Dark),
            _ => None,
        }
    }

    pub fn background(self) -> Background {
        match self {
            Surface::Light => Background::Transparent,
            Surface::Dark => Background::TransparentOnDark,
        }
    }

    /// Directory name the baked assets for this surface live under.
    pub fn asset_suffix(self) -> &'static str {
        match self {
            Surface::Light => "",
            Surface::Dark => "_dark",
        }
    }
}

/// Legacy constructors (`ArtworkCatalogue::entries`) are addressed by
/// underscore name; the web ids use hyphens.
pub fn catalogue_name(artwork_id: &str) -> String {
    artwork_id.trim().replace('-', "_")
}

pub fn artwork_id(catalogue_name: &str) -> String {
    catalogue_name.replace('_', "-")
}

/// Kebab-case ids of everything buildable: the contract catalogue first,
/// then the legacy studies (`wash`, `glaze-pair`) that `by_id` does not know.
pub fn catalogue_ids() -> Vec<String> {
    let mut ids: Vec<String> = ArtworkCatalogue::ids()
        .iter()
        .map(|id| id.to_string())
        .collect();
    for entry in ArtworkCatalogue::entries() {
        let id = artwork_id(entry.name);
        if !ids.contains(&id) {
            ids.push(id);
        }
    }
    ids
}

/// A palette by built-in name (`moonlight`, or `moonlight_dark` for the
/// subtractive variant tuned to dark grounds), or a `PaletteDoc` JSON object.
pub fn parse_palette(palette: &str) -> Result<Palette, SceneToolError> {
    let trimmed = palette.trim();
    if trimmed.starts_with('{') {
        let doc: PaletteDoc = serde_json::from_str(trimmed)
            .map_err(|e| SceneToolError::InvalidPalette(e.to_string()))?;
        return palette_from_doc(doc);
    }
    Palette::by_name(trimmed).ok_or_else(|| SceneToolError::UnknownPalette(trimmed.to_string()))
}

fn palette_from_doc(doc: PaletteDoc) -> Result<Palette, SceneToolError> {
    let mut entries = Vec::with_capacity(doc.entries.len());
    for e in doc.entries {
        let role = match e.role.as_str() {
            "base_wash" => PigmentRole::BaseWash,
            "shadow" => PigmentRole::Shadow,
            "accent" => PigmentRole::Accent,
            "glow" => PigmentRole::Glow,
            other => {
                return Err(SceneToolError::InvalidPalette(format!(
                    "unknown pigment role {other:?}"
                )));
            }
        };
        entries.push(PaletteEntry {
            pigment: Pigment {
                name: e.pigment.name,
                k: Rgb(e.pigment.k),
                s: Rgb(e.pigment.s),
                density: e.pigment.density,
                staining_power: e.pigment.staining_power,
                granulation: e.pigment.granulation,
            },
            role,
        });
    }
    if entries.is_empty() {
        return Err(SceneToolError::InvalidPalette(
            "palette has no entries".into(),
        ));
    }
    Ok(Palette::new(doc.name, entries))
}

/// Concentration multiplier for the legacy studies, which take no intensity
/// of their own. Unity at [`DEFAULT_INTENSITY`]; the low end keeps a visible
/// wash rather than fading to nothing.
pub fn intensity_factor(intensity: f32) -> f32 {
    let i = if intensity.is_finite() {
        intensity.clamp(0.0, 1.0)
    } else {
        DEFAULT_INTENSITY
    };
    if i <= DEFAULT_INTENSITY {
        0.4 + 0.6 * i / DEFAULT_INTENSITY
    } else {
        1.0 + (i - DEFAULT_INTENSITY) / (1.0 - DEFAULT_INTENSITY) * 0.25
    }
}

pub fn apply_intensity(scene: &mut Scene, intensity: f32) {
    let factor = intensity_factor(intensity);
    if (factor - 1.0).abs() < 1e-6 {
        return;
    }
    for event in &mut scene.timeline.events {
        let concentration = match &mut event.op {
            Operation::Brush(b) => Some(&mut b.concentration),
            Operation::Dab(d) => Some(&mut d.concentration),
            _ => None,
        };
        if let Some(concentration) = concentration {
            *concentration = (*concentration * factor).clamp(0.0, MAX_CONCENTRATION);
        }
    }
}

/// The catalogue authors for the ground (`by_id_for`); the legacy studies
/// only have their background swapped.
fn build(
    artwork_id: &str,
    seed: Seed,
    palette: &Palette,
    intensity: f32,
    detail: DetailLevel,
    background: Background,
    sim_resolution: Option<u32>,
) -> Option<Scene> {
    let id = artwork_id.trim();
    if let Some(scene) = ArtworkCatalogue::by_id_for_with_resolution(
        id,
        seed,
        palette,
        intensity,
        detail,
        background,
        sim_resolution,
    ) {
        return Some(scene);
    }
    let mut scene = ArtworkCatalogue::build(&catalogue_name(id), seed, palette.clone())?;
    apply_intensity(&mut scene, intensity);
    scene.background = background;
    if let Some(res) = sim_resolution {
        scene.sim_resolution = SimResolution(res.clamp(SimResolution::MIN, SimResolution::MAX));
    }
    Some(scene)
}

pub fn catalogue_scene(
    artwork_id: &str,
    seed: Seed,
    palette: &str,
    intensity: f32,
    detail: DetailLevel,
    surface: Surface,
) -> Result<Scene, SceneToolError> {
    catalogue_scene_with_resolution(artwork_id, seed, palette, intensity, detail, surface, None)
}

/// [`catalogue_scene`] with the simulation resolution overridden; `None`
/// keeps the detail's default.
pub fn catalogue_scene_with_resolution(
    artwork_id: &str,
    seed: Seed,
    palette: &str,
    intensity: f32,
    detail: DetailLevel,
    surface: Surface,
    sim_resolution: Option<u32>,
) -> Result<Scene, SceneToolError> {
    let palette = parse_palette(palette)?;
    let scene = build(
        artwork_id,
        seed,
        &palette,
        intensity,
        detail,
        surface.background(),
        sim_resolution,
    )
    .ok_or_else(|| SceneToolError::UnknownArtwork(artwork_id.to_string()))?;
    scene
        .validate()
        .map_err(|e| SceneToolError::InvalidScene(format!("{e:?}")))?;
    Ok(scene)
}

pub fn catalogue_scene_json(
    artwork_id: &str,
    seed: Seed,
    palette: &str,
    intensity: f32,
    detail: DetailLevel,
    surface: Surface,
) -> Result<String, SceneToolError> {
    catalogue_scene_json_with_resolution(
        artwork_id, seed, palette, intensity, detail, surface, None,
    )
}

pub fn catalogue_scene_json_with_resolution(
    artwork_id: &str,
    seed: Seed,
    palette: &str,
    intensity: f32,
    detail: DetailLevel,
    surface: Surface,
    sim_resolution: Option<u32>,
) -> Result<String, SceneToolError> {
    let scene = catalogue_scene_with_resolution(
        artwork_id,
        seed,
        palette,
        intensity,
        detail,
        surface,
        sim_resolution,
    )?;
    scene_to_json(&scene).map_err(|e| SceneToolError::InvalidScene(e.to_string()))
}

/// A watercolour scene for a Lucide icon: parses the element list and
/// authors it with the catalogue's stencil-and-fill mapping. `hints_json` is
/// the per-icon tuning (`""` keeps the defaults).
#[allow(clippy::too_many_arguments)]
pub fn icon_scene(
    elements_json: &str,
    name: &str,
    seed: Seed,
    palette: &str,
    intensity: f32,
    detail: DetailLevel,
    surface: Surface,
    sim_resolution: Option<u32>,
    hints_json: &str,
) -> Result<Scene, SceneToolError> {
    let palette = parse_palette(palette)?;
    let elements = parse_icon_elements(elements_json)
        .map_err(|e| SceneToolError::InvalidIcon(e.to_string()))?;
    let hints =
        parse_icon_hints(hints_json).map_err(|e| SceneToolError::InvalidIcon(e.to_string()))?;
    let scene = svg_icon_scene(
        name,
        &elements,
        seed,
        &palette,
        intensity,
        detail,
        surface.background(),
        sim_resolution,
        &hints,
    );
    scene
        .validate()
        .map_err(|e| SceneToolError::InvalidScene(format!("{e:?}")))?;
    Ok(scene)
}

/// [`icon_scene`] as a scene document.
#[allow(clippy::too_many_arguments)]
pub fn icon_scene_json(
    elements_json: &str,
    name: &str,
    seed: Seed,
    palette: &str,
    intensity: f32,
    detail: DetailLevel,
    surface: Surface,
    sim_resolution: Option<u32>,
    hints_json: &str,
) -> Result<String, SceneToolError> {
    let scene = icon_scene(
        elements_json,
        name,
        seed,
        palette,
        intensity,
        detail,
        surface,
        sim_resolution,
        hints_json,
    )?;
    scene_to_json(&scene).map_err(|e| SceneToolError::InvalidScene(e.to_string()))
}

/// Stacks equally sized frames top to bottom into one image.
pub fn stitch_vertical(frames: &[Image]) -> Result<Image, SceneToolError> {
    let first = frames
        .first()
        .ok_or_else(|| SceneToolError::InvalidStrip("no frames".into()))?;
    if frames.len() as u32 > MAX_STRIP_FRAMES {
        return Err(SceneToolError::InvalidStrip(format!(
            "{} frames exceeds the {MAX_STRIP_FRAMES} frame cap",
            frames.len()
        )));
    }
    if first.width > MAX_STRIP_FRAME_EDGE || first.height > MAX_STRIP_FRAME_EDGE {
        return Err(SceneToolError::InvalidStrip(format!(
            "{}x{} exceeds the {MAX_STRIP_FRAME_EDGE} px frame edge cap",
            first.width, first.height
        )));
    }
    let mut out = Image::new(first.width, first.height * frames.len() as u32);
    let mut offset = 0;
    for frame in frames {
        if frame.width != first.width || frame.height != first.height {
            return Err(SceneToolError::InvalidStrip("frame sizes differ".into()));
        }
        out.rgba[offset..offset + frame.rgba.len()].copy_from_slice(&frame.rgba);
        offset += frame.rgba.len();
    }
    Ok(out)
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BakedManifest {
    pub version: u32,
    pub frames: u32,
    pub width: u32,
    pub height: u32,
    pub duration_ms: u32,
    pub layout: &'static str,
}

impl BakedManifest {
    pub fn vertical(frames: u32, width: u32, height: u32, duration_ms: u32) -> Self {
        BakedManifest {
            version: BAKED_MANIFEST_VERSION,
            frames,
            width,
            height,
            duration_ms,
            layout: "vertical",
        }
    }

    pub fn to_json(&self) -> String {
        serde_json::to_string(self).expect("manifest serialises")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nocturne_watercolour_infra::document::parse_scene_json;

    const LARGE: DetailLevel = DetailLevel::Large;

    #[test]
    fn every_id_builds_on_both_surfaces_with_the_same_palette() {
        let ids = catalogue_ids();
        assert!(ids.contains(&"crescent-moon".to_string()));
        assert!(ids.contains(&"wash".to_string()));
        assert!(ids.iter().all(|id| !id.contains('_')));
        for id in ids {
            for surface in [Surface::Light, Surface::Dark] {
                let scene = catalogue_scene(&id, Seed(7), "moonlight", 0.7, LARGE, surface)
                    .unwrap_or_else(|e| panic!("{id} {surface:?}: {e}"));
                assert_eq!(scene.background, surface.background(), "{id}");
                assert_eq!(scene.palette.name, "moonlight", "{id}");
            }
            assert!(
                catalogue_scene_json(
                    &id,
                    Seed(7),
                    "dusk_dark",
                    0.7,
                    DetailLevel::Small,
                    Surface::Light
                )
                .is_ok(),
                "{id} small"
            );
        }
    }

    /// `createCatalogueInstance` and `createIconInstance` play the scene they
    /// author; `createInstance` plays that scene's document read back. They
    /// must be the same scene, to the bit.
    #[test]
    fn an_authored_scene_is_the_scene_its_document_reads_back_as() {
        let clock = r#"[["circle",{"cx":"12","cy":"12","r":"10"}],["path",{"d":"M12 6v6l4 2"}]]"#;
        for detail in DetailLevel::ALL {
            for surface in [Surface::Light, Surface::Dark] {
                for id in catalogue_ids() {
                    let scene = catalogue_scene_with_resolution(
                        &id,
                        Seed(11),
                        "dusk",
                        0.8,
                        detail,
                        surface,
                        None,
                    )
                    .unwrap();
                    let json = scene_to_json(&scene).unwrap();
                    assert_eq!(parse_scene_json(&json).unwrap(), scene, "{id} {detail:?}");
                }
                let scene = icon_scene(
                    clock,
                    "clock",
                    Seed(3),
                    "moonlight",
                    0.7,
                    detail,
                    surface,
                    Some(200),
                    r#"{"fill":[0]}"#,
                )
                .unwrap();
                let json = scene_to_json(&scene).unwrap();
                assert_eq!(parse_scene_json(&json).unwrap(), scene, "clock {detail:?}");
            }
        }
    }

    #[test]
    fn icon_scene_builds_and_rejects_bad_elements() {
        let clock = r#"[["circle",{"cx":"12","cy":"12","r":"10"}],["path",{"d":"M12 6v6l4 2"}]]"#;
        for surface in [Surface::Light, Surface::Dark] {
            let scene = icon_scene_json(
                clock,
                "clock",
                Seed(7),
                "moonlight",
                0.7,
                LARGE,
                surface,
                None,
                "",
            )
            .unwrap();
            assert!(scene.contains("\"id\": \"lucide-clock-moonlight-7\""));
            assert!(parse_scene_json(&scene).is_ok());
        }
        let e = icon_scene_json(
            "[[",
            "clock",
            Seed(1),
            "moonlight",
            0.7,
            LARGE,
            Surface::Light,
            None,
            "",
        )
        .unwrap_err();
        assert!(e.to_string().starts_with("InvalidIcon: "));
    }

    #[test]
    fn icon_scene_hints_change_the_bodies() {
        let database = r#"[["ellipse",{"cx":"12","cy":"5","rx":"9","ry":"3"}],["path",{"d":"M3 5V19A9 3 0 0 0 21 19V5"}],["path",{"d":"M3 12A9 3 0 0 0 21 12"}]]"#;
        let plain = icon_scene_json(
            database,
            "database",
            Seed(7),
            "moonlight",
            0.7,
            LARGE,
            Surface::Light,
            None,
            "",
        )
        .unwrap();
        let filled = icon_scene_json(
            database,
            "database",
            Seed(7),
            "moonlight",
            0.7,
            LARGE,
            Surface::Light,
            None,
            r#"{"fill":[1]}"#,
        )
        .unwrap();
        assert_ne!(plain, filled);
        let masks = |json: &str| {
            let scene = parse_scene_json(json).unwrap();
            scene
                .timeline
                .events
                .iter()
                .filter(|e| matches!(&e.op, Operation::SetMask(_)))
                .count()
        };
        assert_eq!(masks(&plain), 1);
        assert_eq!(masks(&filled), 2);
        let bad = icon_scene_json(
            database,
            "database",
            Seed(7),
            "moonlight",
            0.7,
            LARGE,
            Surface::Light,
            None,
            r#"{"markRole":"neon"}"#,
        )
        .unwrap_err();
        assert!(bad.to_string().starts_with("InvalidIcon: "));
    }

    #[test]
    fn unknown_artwork_and_palette_are_typed() {
        let light = Surface::Light;
        let e =
            catalogue_scene_json("no-such-thing", Seed(1), "dusk", 0.5, LARGE, light).unwrap_err();
        assert!(e.to_string().starts_with("UnknownArtwork: "));
        let e = catalogue_scene_json("wash", Seed(1), "neon", 0.5, LARGE, light).unwrap_err();
        assert!(e.to_string().starts_with("UnknownPalette: "));
        let e = parse_palette("{\"name\": 1}").unwrap_err();
        assert!(e.to_string().starts_with("InvalidPalette: "));
    }

    #[test]
    fn palette_json_document_is_accepted() {
        let scene = catalogue_scene("wash", Seed(1), "ember", 0.7, LARGE, Surface::Light).unwrap();
        let doc = nocturne_watercolour_infra::document::to_document(&scene);
        let json = serde_json::to_string(&doc.palette).unwrap();
        assert_eq!(parse_palette(&json).unwrap(), Palette::ember());
    }

    #[test]
    fn surface_and_detail_parse() {
        assert_eq!(Surface::parse("light"), Some(Surface::Light));
        assert_eq!(Surface::parse(""), Some(Surface::Light));
        assert_eq!(Surface::parse("Dark"), Some(Surface::Dark));
        assert_eq!(Surface::parse("dim"), None);
        assert_eq!(Surface::Dark.background(), Background::TransparentOnDark);
        assert_eq!(Surface::Dark.asset_suffix(), "_dark");
        assert_eq!(parse_detail("small"), Some(DetailLevel::Small));
        assert_eq!(parse_detail("Medium"), Some(DetailLevel::Medium));
        assert_eq!(parse_detail(""), Some(DetailLevel::Large));
        assert_eq!(parse_detail("ExtraLarge"), Some(DetailLevel::ExtraLarge));
        assert_eq!(parse_detail("extra_large"), Some(DetailLevel::ExtraLarge));
        assert_eq!(parse_detail("huge"), None);
        assert_eq!(detail_for_edge_px(32), DetailLevel::Small);
        assert_eq!(detail_for_edge_px(128), DetailLevel::Medium);
        assert_eq!(detail_for_edge_px(256), DetailLevel::Large);
        assert_eq!(detail_for_edge_px(319), DetailLevel::Large);
        assert_eq!(detail_for_edge_px(320), DetailLevel::ExtraLarge);
        assert_eq!(detail_for_edge_px(512), DetailLevel::ExtraLarge);
    }

    #[test]
    fn resolution_override_replaces_the_detail_default_and_is_clamped() {
        let light = Surface::Light;
        let base = catalogue_scene("wash", Seed(3), "water", 0.7, LARGE, light).unwrap();
        assert_eq!(base.sim_resolution.0, DetailLevel::Large.sim_resolution());
        let fine =
            catalogue_scene_with_resolution("wash", Seed(3), "water", 0.7, LARGE, light, Some(384))
                .unwrap();
        assert_eq!(fine.sim_resolution.0, 384);
        let clamped = catalogue_scene_with_resolution(
            "wash",
            Seed(3),
            "water",
            0.7,
            LARGE,
            light,
            Some(2048),
        )
        .unwrap();
        assert_eq!(clamped.sim_resolution.0, SimResolution::MAX);
        let clamped_lo =
            catalogue_scene_with_resolution("wash", Seed(3), "water", 0.7, LARGE, light, Some(8))
                .unwrap();
        assert_eq!(clamped_lo.sim_resolution.0, SimResolution::MIN);
        assert_eq!(
            base.timeline, fine.timeline,
            "resolution does not re-author the scene"
        );
    }

    #[test]
    fn intensity_is_unity_at_default_and_monotone() {
        assert!((intensity_factor(DEFAULT_INTENSITY) - 1.0).abs() < 1e-6);
        let mut last = 0.0;
        for i in 0..=10 {
            let f = intensity_factor(i as f32 / 10.0);
            assert!(f >= last);
            last = f;
        }
        assert!(intensity_factor(0.0) > 0.3);
        assert!(intensity_factor(1.0) <= 1.25 + 1e-6);
        assert_eq!(intensity_factor(f32::NAN), 1.0);
    }

    #[test]
    fn legacy_study_intensity_scales_brush_concentration_and_stays_valid() {
        let light = Surface::Light;
        let base =
            catalogue_scene("wash", Seed(3), "water", DEFAULT_INTENSITY, LARGE, light).unwrap();
        let strong = catalogue_scene("wash", Seed(3), "water", 1.0, LARGE, light).unwrap();
        let conc = |s: &Scene| -> Vec<f32> {
            s.timeline
                .events
                .iter()
                .filter_map(|e| match &e.op {
                    Operation::Brush(b) => Some(b.concentration),
                    _ => None,
                })
                .collect()
        };
        let (a, b) = (conc(&base), conc(&strong));
        assert_eq!(a.len(), b.len());
        assert!(
            a.iter()
                .zip(&b)
                .all(|(x, y)| y > x && *y <= MAX_CONCENTRATION)
        );
        assert_eq!(strong.validate(), Ok(()));
    }

    #[test]
    fn stitch_stacks_frames_and_enforces_caps() {
        let mut a = Image::new(2, 1);
        a.rgba[0] = 1.0;
        let mut b = Image::new(2, 1);
        b.rgba[4] = 0.5;
        let strip = stitch_vertical(&[a.clone(), b]).unwrap();
        assert_eq!((strip.width, strip.height), (2, 2));
        assert_eq!(strip.rgba[0], 1.0);
        assert_eq!(strip.rgba[8 + 4], 0.5);
        let too_many: Vec<Image> = (0..17).map(|_| a.clone()).collect();
        assert!(stitch_vertical(&too_many).is_err());
        assert!(stitch_vertical(&[Image::new(257, 1)]).is_err());
        assert!(stitch_vertical(&[a, Image::new(3, 1)]).is_err());
    }

    #[test]
    fn manifest_matches_the_documented_shape() {
        let json = BakedManifest::vertical(12, 256, 256, 600).to_json();
        assert_eq!(
            json,
            r#"{"version":1,"frames":12,"width":256,"height":256,"durationMs":600,"layout":"vertical"}"#
        );
    }
}
