//! Renders every Lucide icon in `tests/fixtures/lucide-icons.json` at Small and
//! Large on light and dark grounds, and prints element/subpath/point counts
//! with build and render timings:
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example render_lucide --release -- <out_dir> [icon] [palette] [hints.json]
//! ```
//!
//! Before rendering, each icon's flattened subpaths are listed as
//! `idx closed|open length` so hint authors can pick `fill` indices. An
//! optional hints file (keyed by icon name, camelCase fields, same shape as
//! the TS `ICON_HINTS` table) is applied to the matching icons.
//!
//! Small renders at 48 px then upscaled four times with nearest-neighbour so
//! legibility at icon size can be judged without squinting. Files land as
//! `<out>/<icon>/<icon>-lucide-<detail>-<ground>.png`. Run
//! `render_catalogue` for the hand-authored counterparts into the same
//! directories so they sit beside them.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use nocturne_watercolour_core::application::{CpuEngine, Exporter, Playback, Renderer, Simulator};
use nocturne_watercolour_core::domain::{Background, Image, Palette, Rgb, Scene, Seed};
use nocturne_watercolour_infra::authoring::{
    ArtworkCatalogue, DetailLevel, FLATTEN_TOLERANCE, IconHints, parse_icon_elements,
    parse_icon_hints, svg_icon_scene,
};
use nocturne_watercolour_infra::export::{PngExporter, srgb_to_linear};
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

/// Catalogue ids that read the same object as a Lucide icon in the test set,
/// so the orchestrator can compare the generated scene against the hand
/// authored one side by side.
const HAND_COUNTERPARTS: &[(&str, &str)] = &[
    ("clock", "clock"),
    ("calendar", "calendar"),
    ("heart", "heart"),
    ("key", "key"),
    ("phone", "phone"),
    ("bell", "alarm-bell"),
];

const SEED: Seed = Seed(42);
const DURATION_MS: f32 = 4000.0;
const SMALL_UPSCALE: u32 = 4;

fn hex(rgb: u32) -> Rgb {
    Rgb::new(
        srgb_to_linear(((rgb >> 16) & 0xff) as f32 / 255.0),
        srgb_to_linear(((rgb >> 8) & 0xff) as f32 / 255.0),
        srgb_to_linear((rgb & 0xff) as f32 / 255.0),
    )
}

fn write_png(dir: &Path, name: &str, image: &Image) {
    let bytes = PngExporter.encode(image).expect("png encode");
    fs::write(dir.join(name), bytes).expect("write png");
}

/// The hint entry for `name`, or the default tuning when the file has none.
fn hints_for(name: &str, table: &BTreeMap<String, serde_json::Value>) -> IconHints {
    table
        .get(name)
        .map(|v| parse_icon_hints(&v.to_string()).unwrap_or_else(|e| panic!("{name}: {e}")))
        .unwrap_or_default()
}

fn upscale_nearest(image: &Image, factor: u32) -> Image {
    let mut out = Image::new(image.width * factor, image.height * factor);
    for y in 0..out.height {
        for x in 0..out.width {
            let px = image.pixel(x / factor, y / factor);
            let o = ((y as usize) * (out.width as usize) + x as usize) * 4;
            out.rgba[o..o + 4].copy_from_slice(&px);
        }
    }
    out
}

/// Runs `scene` to the end on `engine` and renders it at `w`x`h`; returns the
/// engine, the image and the simulate / render times in milliseconds.
fn finished_frame<E: Simulator + Renderer>(
    engine: E,
    scene: &Scene,
    w: u32,
    h: u32,
) -> (E, Image, f64, f64) {
    let t = Instant::now();
    let mut pb = Playback::new(engine, scene.clone(), DURATION_MS).expect("playback");
    pb.finish_immediately().expect("finish");
    let sim_ms = t.elapsed().as_secs_f64() * 1000.0;
    let t = Instant::now();
    let image = pb.simulator().render(w, h).expect("render");
    let render_ms = t.elapsed().as_secs_f64() * 1000.0;
    (pb.into_simulator(), image, sim_ms, render_ms)
}

fn main() {
    let mut args = std::env::args().skip(1);
    let out_dir: PathBuf = args
        .next()
        .map(PathBuf::from)
        .expect("usage: render_lucide <out_dir> [icon] [palette] [hints.json]");
    let only = args.next();
    let palette = args
        .next()
        .map(|name| Palette::by_name(&name).unwrap_or_else(|| panic!("unknown palette {name}")))
        .unwrap_or_else(Palette::slate);
    let icon_hints: BTreeMap<String, serde_json::Value> = match args.next() {
        Some(path) => {
            let json = fs::read_to_string(&path).expect("read hints file");
            serde_json::from_str(&json).expect("hints file is a JSON object keyed by icon name")
        }
        None => BTreeMap::new(),
    };
    fs::create_dir_all(&out_dir).expect("create out dir");

    let json = fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/tests/fixtures/lucide-icons.json"
    ))
    .expect("tests/fixtures/lucide-icons.json");
    let data: BTreeMap<String, serde_json::Value> = serde_json::from_str(&json).expect("icon map");
    let names: Vec<String> = data
        .keys()
        .filter(|name| only.as_deref().is_none_or(|o| o == *name))
        .cloned()
        .collect();
    if names.is_empty() {
        eprintln!("unknown icon {:?}; known: {:?}", only, data.keys());
        std::process::exit(1);
    }

    println!("\nsubpath listing (idx closed|open length in 24-grid units):");
    for name in &names {
        let elements =
            parse_icon_elements(&data[name].to_string()).unwrap_or_else(|e| panic!("{name}: {e}"));
        let subpaths: Vec<_> = elements
            .iter()
            .flat_map(|e| e.flatten(FLATTEN_TOLERANCE))
            .collect();
        println!("{name}:");
        for (i, s) in subpaths.iter().enumerate() {
            println!(
                "  {i:>2} {:<6} {:.3}",
                if s.closed { "closed" } else { "open" },
                s.length()
            );
        }
    }

    let t0 = Instant::now();
    let Some(ctx) = GpuContext::try_new().expect("gpu context") else {
        eprintln!("no GPU adapter available");
        std::process::exit(2);
    };
    let mut gpu = GpuEngine::new(ctx.clone()).expect("gpu engine");
    println!(
        "device init: {:.1} ms ({} via {:?})",
        t0.elapsed().as_secs_f64() * 1000.0,
        ctx.adapter_name(),
        ctx.backend()
    );

    let grounds = [
        ("light", hex(0xf7f5f0), false),
        ("dark", hex(0x0f1420), true),
    ];

    println!(
        "{:<10} {:<3} {:<3} {:<5}  {:<7}  {:<6} {:<6}  {:<6} {:<6}",
        "icon", "el", "sp", "pts", "build", "simSm", "rndSm", "simLg", "rndLg"
    );
    for name in &names {
        let elements =
            parse_icon_elements(&data[name].to_string()).unwrap_or_else(|e| panic!("{name}: {e}"));
        let hints = hints_for(name, &icon_hints);
        let subpaths: Vec<_> = elements
            .iter()
            .flat_map(|e| e.flatten(FLATTEN_TOLERANCE))
            .collect();
        let points: usize = subpaths.iter().map(|s| s.points.len()).sum();

        let icon_dir = out_dir.join(name);
        fs::create_dir_all(&icon_dir).expect("create icon dir");
        let t = Instant::now();
        let _ = svg_icon_scene(
            name,
            &elements,
            SEED,
            &palette,
            0.7,
            DetailLevel::Large,
            Background::Transparent,
            None,
            &hints,
        );
        let build_ms = t.elapsed().as_secs_f64() * 1000.0;

        let mut row = format!(
            "{name:<10} {:<3} {:<3} {:<5}  {build_ms:>7.2}",
            elements.len(),
            subpaths.len(),
            points,
        );
        for (tag, detail) in [("sm", DetailLevel::Small), ("lg", DetailLevel::Large)] {
            let (w, h) = if detail == DetailLevel::Small {
                (48, 48)
            } else {
                (512, 512)
            };
            let mut sim = 0.0;
            let mut render = 0.0;
            for (ground, colour, dark) in grounds {
                let background = if dark {
                    Background::TransparentOnDark
                } else {
                    Background::Transparent
                };
                let scene = svg_icon_scene(
                    name, &elements, SEED, &palette, 0.7, detail, background, None, &hints,
                );
                scene.validate().expect("valid scene");
                gpu.load(&scene).expect("load");
                let forwarded =
                    gpu.read_grid().expect("read grid").composite_mode == scene.composite_mode();
                let (mut image, sim_ms, render_ms) = if forwarded {
                    let (g, image, sim_ms, render_ms) = finished_frame(gpu, &scene, w, h);
                    gpu = g;
                    (image, sim_ms, render_ms)
                } else {
                    let (_, image, sim_ms, render_ms) =
                        finished_frame(CpuEngine::default(), &scene, w, h);
                    (image, sim_ms, render_ms)
                };
                if detail == DetailLevel::Small {
                    image = upscale_nearest(&image, SMALL_UPSCALE);
                }
                let file = format!("{name}-lucide-{}-{ground}.png", detail.name());
                let composited = image.composite_over(colour);
                write_png(&icon_dir, &file, &composited);
                sim = sim_ms;
                render = render_ms;
            }
            if let Some((_, hand_id)) = HAND_COUNTERPARTS.iter().find(|(l, _)| *l == *name) {
                for (ground, colour, dark) in grounds {
                    let background = if dark {
                        Background::TransparentOnDark
                    } else {
                        Background::Transparent
                    };
                    let scene = ArtworkCatalogue::by_id_for(
                        hand_id, SEED, &palette, 0.7, detail, background,
                    )
                    .expect("hand-authored counterpart");
                    gpu.load(&scene).expect("load");
                    let forwarded = gpu.read_grid().expect("read grid").composite_mode
                        == scene.composite_mode();
                    let (mut image, _, _) = if forwarded {
                        let (g, image, sim_ms, render_ms) = finished_frame(gpu, &scene, w, h);
                        gpu = g;
                        (image, sim_ms, render_ms)
                    } else {
                        let (_, image, sim_ms, render_ms) =
                            finished_frame(CpuEngine::default(), &scene, w, h);
                        (image, sim_ms, render_ms)
                    };
                    if detail == DetailLevel::Small {
                        image = upscale_nearest(&image, SMALL_UPSCALE);
                    }
                    let file = format!("{name}-hand-{}-{ground}.png", detail.name());
                    let composited = image.composite_over(colour);
                    write_png(&icon_dir, &file, &composited);
                }
            }
            row.push_str(&format!("  {tag} {sim:>5.1} {render:>5.1}"));
        }
        println!("{row}");
    }
    println!(
        "\ntotal {:.0} ms; wrote PNGs to {}",
        t0.elapsed().as_secs_f64() * 1000.0,
        out_dir.display()
    );
}
