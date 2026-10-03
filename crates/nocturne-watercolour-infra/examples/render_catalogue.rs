//! Renders every catalogue id (or one) on the GPU at each detail level,
//! composited over a light and a dark ground, and prints tick counts and
//! timings:
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example render_catalogue --release -- <out_dir> [id] [palette]
//! ```
//!
//! Large renders at 512 px on the long side, Medium at 160, Small at 48 and
//! then upscaled four times with nearest-neighbour so legibility at icon
//! size can be judged without squinting. The dark ground renders the normal
//! palette with `Background::TransparentOnDark` (luminous compositing); when
//! the GPU engine does not yet forward that mode it falls back to the CPU
//! reference for those renders and says so.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use nocturne_watercolour_core::application::{CpuEngine, Exporter, Playback, Renderer, Simulator};
use nocturne_watercolour_core::domain::{Background, Image, Palette, Rgb, Scene, Seed};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DEFAULT_INTENSITY, DetailLevel};
use nocturne_watercolour_infra::export::{PngExporter, srgb_to_linear};
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

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

fn long_side(detail: DetailLevel) -> u32 {
    match detail {
        DetailLevel::Small => 48,
        DetailLevel::Medium => 160,
        DetailLevel::Large => 512,
        DetailLevel::ExtraLarge => 768,
    }
}

/// Output size with the scene's aspect and `long` on its longer side.
fn output_size(scene: &Scene, long: u32) -> (u32, u32) {
    let (w, h) = (scene.size_hint.width as f32, scene.size_hint.height as f32);
    if w >= h {
        (long, ((long as f32 * h / w).round() as u32).max(1))
    } else {
        (((long as f32 * w / h).round() as u32).max(1), long)
    }
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

/// All renders of one detail level and ground on one sheet, `columns`
/// across, each centred in a cell of the level's long side.
fn contact_sheet(images: &[Image], long: u32, columns: u32, ground: Rgb) -> Image {
    let rows = (images.len() as u32).div_ceil(columns).max(1);
    let mut sheet = Image::new(columns * long, rows * long);
    for px in sheet.rgba.chunks_exact_mut(4) {
        px.copy_from_slice(&[ground.0[0], ground.0[1], ground.0[2], 1.0]);
    }
    for (i, img) in images.iter().enumerate() {
        let ox = (i as u32 % columns) * long + (long - img.width.min(long)) / 2;
        let oy = (i as u32 / columns) * long + (long - img.height.min(long)) / 2;
        for y in 0..img.height.min(long) {
            for x in 0..img.width.min(long) {
                let src = img.pixel(x, y);
                let o = (((oy + y) as usize) * (sheet.width as usize) + (ox + x) as usize) * 4;
                sheet.rgba[o..o + 4].copy_from_slice(&src);
            }
        }
    }
    sheet
}

fn default_palette(id: &str) -> Palette {
    match id {
        "crescent-moon" | "moonlit-shoreline" | "header-motif" | "selection-edge" => {
            Palette::moonlight()
        }
        "connected-shores" | "magnifying-glass" | "avatar-wash" | "report-pages" => {
            Palette::water()
        }
        "distant-mountains" => Palette::moonlight(),
        "overlapping-shapes" | "linked-rings" => Palette::dusk(),
        "alarm-bell" | "tab-underline" => Palette::ember(),
        "confirmation-mark" | "confirmation-background" => Palette::moss(),
        _ => Palette::slate(),
    }
}

fn main() {
    let mut args = std::env::args().skip(1);
    let out_dir: PathBuf = args
        .next()
        .map(PathBuf::from)
        .expect("usage: render_catalogue <out_dir> [id] [palette]");
    let only = args.next();
    let palette_override = args
        .next()
        .map(|name| Palette::by_name(&name).unwrap_or_else(|| panic!("unknown palette {name}")));
    fs::create_dir_all(&out_dir).expect("create out dir");

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
    let ids: Vec<&str> = ArtworkCatalogue::ids()
        .iter()
        .copied()
        .filter(|id| only.as_deref().is_none_or(|o| o == *id))
        .collect();
    if ids.is_empty() {
        eprintln!(
            "unknown id {:?}; known: {:?}",
            only,
            ArtworkCatalogue::ids()
        );
        std::process::exit(1);
    }

    let total = Instant::now();
    let mut cpu_fallbacks = 0u32;
    let mut sheets: Vec<((DetailLevel, &str), Vec<Image>)> = DetailLevel::ALL
        .iter()
        .flat_map(|d| grounds.iter().map(move |g| ((*d, g.0), Vec::new())))
        .collect();
    for id in &ids {
        let base_palette = palette_override
            .clone()
            .unwrap_or_else(|| default_palette(id));
        for detail in DetailLevel::ALL {
            for (ground, colour, dark) in grounds {
                let background = if dark {
                    Background::TransparentOnDark
                } else {
                    Background::Transparent
                };
                let scene = ArtworkCatalogue::by_id_for(
                    id,
                    SEED,
                    &base_palette,
                    DEFAULT_INTENSITY,
                    detail,
                    background,
                )
                .expect("catalogue id");
                scene.validate().expect("valid scene");
                let ticks = scene.timeline.total_ticks;
                let events = scene.timeline.events.len();
                let (w, h) = output_size(&scene, long_side(detail));

                gpu.load(&scene).expect("load");
                let forwarded =
                    gpu.read_grid().expect("read grid").composite_mode == scene.composite_mode();
                let (mut image, sim_ms, render_ms, route) = if forwarded {
                    let (g, image, sim_ms, render_ms) = finished_frame(gpu, &scene, w, h);
                    gpu = g;
                    (image, sim_ms, render_ms, "gpu")
                } else {
                    cpu_fallbacks += 1;
                    let (_, image, sim_ms, render_ms) =
                        finished_frame(CpuEngine::default(), &scene, w, h);
                    (image, sim_ms, render_ms, "cpu")
                };

                if detail == DetailLevel::Small {
                    image = upscale_nearest(&image, SMALL_UPSCALE);
                }
                let name = format!("{id}_{}_{ground}.png", detail.name());
                let composited = image.composite_over(colour);
                write_png(&out_dir, &name, &composited);
                if let Some((_, list)) = sheets.iter_mut().find(|(k, _)| *k == (detail, ground)) {
                    list.push(composited);
                }
                println!(
                    "{id:<24} {:<6} {ground:<5} {route} sim {:>3}^2 {ticks:>3} ticks {events:>2} events  sim {sim_ms:>7.1} ms ({:.3} ms/tick)  render {w}x{h} {render_ms:>6.1} ms",
                    detail.name(),
                    scene.sim_resolution.0,
                    sim_ms / ticks as f64,
                );
            }
        }
    }
    if ids.len() > 1 {
        for ((detail, ground), images) in &sheets {
            let scale = if *detail == DetailLevel::Small {
                SMALL_UPSCALE
            } else {
                1
            };
            let colour = grounds
                .iter()
                .find(|g| g.0 == *ground)
                .map(|g| g.1)
                .unwrap();
            let sheet = contact_sheet(images, long_side(*detail) * scale, 5, colour);
            write_png(
                &out_dir,
                &format!("sheet_{}_{ground}.png", detail.name()),
                &sheet,
            );
        }
    }
    if cpu_fallbacks > 0 {
        println!(
            "\n{cpu_fallbacks} renders went through the CPU reference: GpuEngine::load does not forward the scene's composite mode into the state header yet"
        );
    }
    println!(
        "\ntotal {:.0} ms; wrote PNGs to {}",
        total.elapsed().as_secs_f64() * 1000.0,
        out_dir.display()
    );
}

/// Runs `scene` to the end on `engine` and renders it at `w`x`h`; returns
/// the engine, the image and the simulate / render times in milliseconds.
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
