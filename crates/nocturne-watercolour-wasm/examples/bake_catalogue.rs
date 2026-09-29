//! Bakes the fallback assets `@nocturne/watercolour` ships: the finished
//! frame at 512 and 128 px and a frame strip with its manifest. The shipped
//! set is driven by a small JSON manifest (`scripts/bake-manifest.json` in the
//! web package) that lists the artworks, their default palette, both surfaces
//! and each strip's frame size; the whole catalogue or one artwork can still
//! be baked with the same example.
//!
//! ```text
//! cargo run -p nocturne-watercolour-wasm --example bake_catalogue --release -- <out_dir> [manifest.json]
//! cargo run -p nocturne-watercolour-wasm --example bake_catalogue --release -- <out_dir> [artwork-id]
//! ```
//!
//! With no second argument the whole catalogue is baked (all artworks x all
//! palettes x both surfaces, ~100 MB). The curated set is the manifest's; a
//! strip frame's long edge stays within the 256 px cap and the frame count
//! within 16 (see `scene_tools`).
//!
//! A manifest entry whose id starts with `lucide:` is a Lucide icon: the
//! element list is read from `scripts/lucide-icons.json` (regenerated from the
//! `lucide` npm package by `scripts/lucide-icons.mjs` before cargo runs) and
//! built into a scene with `svg_icon_scene`, so the icon's default palette
//! drives the asset set like any artwork's. The icons lay out under
//! `<out_dir>/lucide-<name>/...` so the browser key is `lucide-<name>`, not
//! the manifest's `lucide:<name>`.
//!
//! Layout: `<out_dir>/<artwork-id>/<palette>/{final-512.png,final-128.png,strip.png,strip.json}`,
//! where `<palette>` is the built-in name for light surfaces and `<name>_dark`
//! the same palette composited in luminous mode for dark ones. Finals and
//! strips are rendered at the artwork's natural aspect (long edge 512 / 128
//! for finals, the manifest's frame size for strips).
//!
//! A staged artwork (`ArtworkCatalogue::stages`) is baked the same way in
//! every mode: one strip frame per stop, sampled one-for-one in ticks
//! (`ProgressCurve::Linear`) instead of on the reveal curve, so frame `k` is
//! exactly the painting after `k` of its stages.
//!
//! The PNGs written here are intermediates. `pnpm bake` runs
//! `src/Web/packages/watercolour/scripts/to-webp.mjs` afterwards, which
//! re-encodes each one as WebP and deletes it, because these washes are mostly
//! soft alpha and PNG stores that badly: 20 MB becomes 4.4 MB. The shipped
//! asset set is `.webp`, and that is what the loader asks for.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use nocturne_watercolour_core::application::{Exporter, Playback, ProgressCurve, Renderer};
use nocturne_watercolour_core::domain::{Palette, Seed};
use nocturne_watercolour_infra::authoring::{
    ArtworkCatalogue, IconHints, IconNode, parse_icon_elements, parse_icon_hints, svg_icon_scene,
};
use nocturne_watercolour_infra::export::{FrameSequence, PngExporter};
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};
use nocturne_watercolour_wasm::scene_tools::{
    BakedManifest, DEFAULT_INTENSITY, DetailLevel, Surface, catalogue_ids, catalogue_scene,
    stitch_vertical,
};
use serde::Deserialize;

/// Baked assets are keyed by artwork and palette only, so one seed serves
/// every host; it matches the showcase's default.
const SEED: Seed = Seed(1610);
const STRIP_FRAMES: u32 = 12;
const STRIP_EDGE: u32 = 256;
const DEFAULT_DURATION_MS: u32 = 600;
const FINAL_LONG_EDGE: u32 = 512;
const FINAL_SMALL_LONG_EDGE: u32 = 128;

/// What `scripts/bake-manifest.json` drives the curated bake with.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BakeManifest {
    version: u32,
    seed: u64,
    intensity: f32,
    detail: String,
    duration_ms: u32,
    strip_frames: u32,
    artworks: Vec<ManifestArtwork>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ManifestArtwork {
    id: String,
    palette: String,
    surfaces: Vec<String>,
    strip_width: u32,
    strip_height: u32,
}

/// A `lucide:<name>` manifest entry: the element list and built-in hints
/// (`scripts/lucide-icons.json` / `scripts/icon-hints.json`, both next to the
/// manifest) the bake builds a scene from.
struct IconBake {
    name: String,
    elements: Vec<IconNode>,
    hints: IconHints,
}

struct ArtworkSpec {
    id: String,
    palettes: Vec<(String, Surface)>,
    strip_frames: u32,
    strip_width: u32,
    strip_height: u32,
    icon: Option<IconBake>,
}

struct BakeConfig {
    seed: Seed,
    intensity: f32,
    detail: DetailLevel,
    duration_ms: u32,
    specs: Vec<ArtworkSpec>,
}

/// A staged artwork gets a frame per stop; everything else `default`.
fn strip_frames(id: &str, default: u32) -> u32 {
    ArtworkCatalogue::stages(id).map_or(default, |stages| stages + 1)
}

/// Scales `(width, height)` so the long edge is `target`, preserving aspect.
fn fit(target: u32, width: u32, height: u32) -> (u32, u32) {
    let long = width.max(height);
    if long == 0 {
        return (0, 0);
    }
    let scale = target as f64 / long as f64;
    (
        (width as f64 * scale).round().max(1.0) as u32,
        (height as f64 * scale).round().max(1.0) as u32,
    )
}

/// Every built-in palette x surface, for the whole-catalogue bake.
fn full_variants() -> Vec<(String, Surface)> {
    Palette::NAMES
        .iter()
        .flat_map(|n| {
            [
                ((*n).to_string(), Surface::Light),
                ((*n).to_string(), Surface::Dark),
            ]
        })
        .collect()
}

/// The `{ "<name>": IconNode }` table `scripts/lucide-icons.mjs` regenerates
/// from the manifest before the bake runs; the Rust side cannot read npm.
fn load_icon_elements(path: &Path) -> BTreeMap<String, Vec<IconNode>> {
    let text = fs::read_to_string(path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let raw: BTreeMap<String, serde_json::Value> =
        serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
    raw.into_iter()
        .map(|(name, value)| {
            let elements = parse_icon_elements(&value.to_string())
                .unwrap_or_else(|e| panic!("{name} element list: {e}"));
            (name, elements)
        })
        .collect()
}

/// The built-in per-icon hints, one entry per name — the same
/// `scripts/icon-hints.json` the browser's `ICON_HINTS` table imports.
fn load_icon_hints(path: &Path) -> BTreeMap<String, IconHints> {
    let text = fs::read_to_string(path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let raw: BTreeMap<String, serde_json::Value> =
        serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
    raw.into_iter()
        .map(|(name, value)| {
            let hints = parse_icon_hints(&value.to_string())
                .unwrap_or_else(|e| panic!("{name} hints: {e}"));
            (name, hints)
        })
        .collect()
}

fn curated(manifest_path: &Path) -> BakeConfig {
    let text = fs::read_to_string(manifest_path).expect("read manifest");
    let manifest: BakeManifest = serde_json::from_str(&text).expect("parse manifest");
    assert_eq!(manifest.version, 1, "unsupported manifest version");
    let detail = match manifest.detail.as_str() {
        "small" => DetailLevel::Small,
        "medium" => DetailLevel::Medium,
        _ => DetailLevel::Large,
    };
    let scripts = manifest_path.parent().unwrap_or_else(|| Path::new("."));
    let elements = load_icon_elements(&scripts.join("lucide-icons.json"));
    let hints = load_icon_hints(&scripts.join("icon-hints.json"));
    let specs = manifest
        .artworks
        .into_iter()
        .map(|a| {
            let surfaces: Vec<Surface> = a
                .surfaces
                .iter()
                .map(|s| Surface::parse(s).expect("manifest surface"))
                .collect();
            let icon = a.id.strip_prefix("lucide:").map(|name| IconBake {
                name: name.to_string(),
                elements: elements
                    .get(name)
                    .unwrap_or_else(|| {
                        panic!("manifest lists {name} but lucide-icons.json has no entry for it")
                    })
                    .clone(),
                hints: hints.get(name).cloned().unwrap_or_default(),
            });
            let id = icon
                .as_ref()
                .map_or(a.id.clone(), |icon| format!("lucide-{}", icon.name));
            ArtworkSpec {
                strip_frames: strip_frames(&id, manifest.strip_frames),
                id,
                palettes: surfaces
                    .into_iter()
                    .map(|surface| (a.palette.clone(), surface))
                    .collect(),
                strip_width: a.strip_width,
                strip_height: a.strip_height,
                icon,
            }
        })
        .collect();
    BakeConfig {
        seed: Seed(manifest.seed),
        intensity: manifest.intensity,
        detail,
        duration_ms: manifest.duration_ms,
        specs,
    }
}

fn whole_catalogue() -> BakeConfig {
    let specs = catalogue_ids()
        .into_iter()
        .map(|id| ArtworkSpec {
            strip_frames: strip_frames(&id, STRIP_FRAMES),
            id,
            palettes: full_variants(),
            strip_width: STRIP_EDGE,
            strip_height: STRIP_EDGE,
            icon: None,
        })
        .collect();
    BakeConfig {
        seed: SEED,
        intensity: DEFAULT_INTENSITY,
        detail: DetailLevel::Large,
        duration_ms: DEFAULT_DURATION_MS,
        specs,
    }
}

fn single_artwork(id: &str) -> BakeConfig {
    let specs = vec![ArtworkSpec {
        id: id.to_string(),
        palettes: full_variants(),
        strip_frames: strip_frames(id, STRIP_FRAMES),
        strip_width: STRIP_EDGE,
        strip_height: STRIP_EDGE,
        icon: None,
    }];
    BakeConfig {
        seed: SEED,
        intensity: DEFAULT_INTENSITY,
        detail: DetailLevel::Large,
        duration_ms: DEFAULT_DURATION_MS,
        specs,
    }
}

fn main() {
    let mut args = std::env::args().skip(1);
    let out_dir: PathBuf = args
        .next()
        .map(PathBuf::from)
        .expect("usage: bake_catalogue <out_dir> [manifest.json|artwork-id]");
    let only = args.next();
    let t0 = Instant::now();
    let ctx = match GpuContext::try_new().expect("gpu context") {
        Some(ctx) => ctx,
        None => {
            eprintln!("no GPU adapter available");
            std::process::exit(2);
        }
    };
    let template = GpuEngine::new(ctx.clone()).expect("gpu engine");
    println!(
        "device init {:.0} ms ({} via {:?})",
        t0.elapsed().as_secs_f64() * 1000.0,
        ctx.adapter_name(),
        ctx.backend()
    );

    let config = match only.as_deref() {
        Some(arg) if arg.ends_with(".json") => {
            println!("manifest {arg}");
            curated(Path::new(arg))
        }
        Some(id) => single_artwork(id),
        None => whole_catalogue(),
    };
    if config.specs.is_empty() {
        eprintln!("unknown artwork {only:?}; known: {:?}", catalogue_ids());
        std::process::exit(1);
    }

    let mut total_bytes = 0u64;
    for spec in config.specs {
        for (palette, surface) in &spec.palettes {
            let started = Instant::now();
            let scene = match &spec.icon {
                Some(icon) => {
                    let palette = Palette::by_name(palette).expect("manifest palette");
                    let scene = svg_icon_scene(
                        &icon.name,
                        &icon.elements,
                        config.seed,
                        &palette,
                        config.intensity,
                        config.detail,
                        surface.background(),
                        None,
                        &icon.hints,
                    );
                    scene.validate().expect("valid icon scene");
                    scene
                }
                None => catalogue_scene(
                    &spec.id,
                    config.seed,
                    palette,
                    config.intensity,
                    config.detail,
                    *surface,
                )
                .expect("scene"),
            };
            let key = format!("{palette}{}", surface.asset_suffix());
            let dir = out_dir.join(&spec.id).join(&key);
            fs::create_dir_all(&dir).expect("create asset dir");

            let mut playback = Playback::new(template.fork(), scene, 1000.0).expect("playback");
            if ArtworkCatalogue::stages(&spec.id).is_some() {
                playback.set_progress_curve(ProgressCurve::Linear);
            }
            let frames = FrameSequence {
                count: spec.strip_frames,
                width: spec.strip_width,
                height: spec.strip_height,
            }
            .render(&mut playback)
            .expect("frames");
            let strip = stitch_vertical(&frames).expect("strip");
            let (final_w, final_h) = fit(FINAL_LONG_EDGE, spec.strip_width, spec.strip_height);
            let (small_w, small_h) =
                fit(FINAL_SMALL_LONG_EDGE, spec.strip_width, spec.strip_height);
            let final_512 = playback
                .simulator()
                .render(final_w, final_h)
                .expect("render 512");
            let final_128 = playback
                .simulator()
                .render(small_w, small_h)
                .expect("render 128");

            let mut write = |name: &str, bytes: Vec<u8>| {
                total_bytes += bytes.len() as u64;
                let size = bytes.len();
                fs::write(dir.join(name), bytes).expect("write asset");
                size
            };
            let s512 = write("final-512.png", PngExporter.encode(&final_512).unwrap());
            let s128 = write("final-128.png", PngExporter.encode(&final_128).unwrap());
            let sstrip = write("strip.png", PngExporter.encode(&strip).unwrap());
            let manifest = BakedManifest::vertical(
                spec.strip_frames,
                spec.strip_width,
                spec.strip_height,
                config.duration_ms,
            );
            write("strip.json", manifest.to_json().into_bytes());
            println!(
                "{}/{}: final-512 {:.0} KB, final-128 {:.0} KB, strip {:.0} KB ({:.0} ms)",
                spec.id,
                key,
                s512 as f64 / 1024.0,
                s128 as f64 / 1024.0,
                sstrip as f64 / 1024.0,
                started.elapsed().as_secs_f64() * 1000.0
            );
        }
    }
    println!(
        "total {:.1} MB in {:.1} s",
        total_bytes as f64 / (1024.0 * 1024.0),
        t0.elapsed().as_secs_f64()
    );
}
