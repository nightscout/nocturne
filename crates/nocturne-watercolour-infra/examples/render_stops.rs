//! Renders a staged artwork at every progress stop on the GPU, light and
//! dark side by side, one row per stop, as a contact sheet for picking by eye:
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example render_stops --release -- <out_dir> <palette> <id>... [--detail large|extralarge] [--width 720]
//! ```
//!
//! A stop `k` of `n` is the tick `k / n` of the timeline, which is where a
//! `ProgressCurve::Linear` seek lands. Writes `<id>.png` per id.

use std::fs;
use std::path::PathBuf;

use nocturne_watercolour_core::application::{Exporter, Playback, ProgressCurve, Renderer};
use nocturne_watercolour_core::domain::{Background, Image, Palette, Rgb, Seed};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DEFAULT_INTENSITY, DetailLevel};
use nocturne_watercolour_infra::export::{PngExporter, srgb_to_linear};
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

const SEED: Seed = Seed(1610);
const STOPS: u32 = 6;
const GUTTER: u32 = 12;

fn hex(rgb: u32) -> Rgb {
    Rgb::new(
        srgb_to_linear(((rgb >> 16) & 0xff) as f32 / 255.0),
        srgb_to_linear(((rgb >> 8) & 0xff) as f32 / 255.0),
        srgb_to_linear((rgb & 0xff) as f32 / 255.0),
    )
}

fn blit(sheet: &mut Image, img: &Image, ox: u32, oy: u32) {
    for y in 0..img.height {
        for x in 0..img.width {
            let o = (((oy + y) as usize) * (sheet.width as usize) + (ox + x) as usize) * 4;
            sheet.rgba[o..o + 4].copy_from_slice(&img.pixel(x, y));
        }
    }
}

fn main() {
    let mut args = std::env::args().skip(1);
    let out_dir = PathBuf::from(
        args.next()
            .expect("usage: render_stops <out_dir> <palette> <id>..."),
    );
    let palette = Palette::by_name(&args.next().expect("palette")).expect("known palette");
    let mut ids = Vec::new();
    let mut detail = DetailLevel::ExtraLarge;
    let mut width = 720u32;
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--detail" => {
                detail = match args.next().as_deref() {
                    Some("large") => DetailLevel::Large,
                    Some("medium") => DetailLevel::Medium,
                    _ => DetailLevel::ExtraLarge,
                }
            }
            "--width" => width = args.next().and_then(|w| w.parse().ok()).expect("width"),
            _ => ids.push(arg),
        }
    }
    fs::create_dir_all(&out_dir).expect("out dir");
    let Some(ctx) = GpuContext::try_new().expect("gpu context") else {
        eprintln!("no GPU adapter available");
        std::process::exit(2);
    };
    let template = GpuEngine::new(ctx.clone()).expect("gpu engine");
    let grounds = [
        (Background::Transparent, hex(0xf7f5f0)),
        (Background::TransparentOnDark, hex(0x0f1420)),
    ];
    for id in &ids {
        let mut columns: Vec<Vec<Image>> = Vec::new();
        for (background, ground) in grounds {
            let scene = ArtworkCatalogue::by_id_for(
                id,
                SEED,
                &palette,
                DEFAULT_INTENSITY,
                detail,
                background,
            )
            .unwrap_or_else(|| panic!("unknown id {id}"));
            let height = (width as f32 * scene.size_hint.height as f32
                / scene.size_hint.width as f32)
                .round() as u32;
            let total = scene.timeline.total_ticks;
            let mut pb = Playback::new(template.fork(), scene, 1000.0).expect("playback");
            pb.set_progress_curve(ProgressCurve::Linear);
            let mut frames = Vec::new();
            for k in 0..=STOPS {
                if k == STOPS {
                    pb.finish_immediately().expect("finish");
                } else {
                    pb.seek_tick(total * k / STOPS).expect("seek");
                }
                let image = pb.simulator().render(width, height).expect("render");
                frames.push(image.composite_over(ground));
            }
            println!("{id} {background:?}: {total} ticks");
            columns.push(frames);
        }
        let (w, h) = (columns[0][0].width, columns[0][0].height);
        let mut sheet = Image::new(2 * w + 3 * GUTTER, (STOPS + 1) * (h + GUTTER) + GUTTER);
        for px in sheet.rgba.chunks_exact_mut(4) {
            px.copy_from_slice(&[0.5, 0.5, 0.5, 1.0]);
        }
        for (c, frames) in columns.iter().enumerate() {
            for (r, img) in frames.iter().enumerate() {
                blit(
                    &mut sheet,
                    img,
                    GUTTER + c as u32 * (w + GUTTER),
                    GUTTER + r as u32 * (h + GUTTER),
                );
            }
        }
        fs::write(
            out_dir.join(format!("{id}.png")),
            PngExporter.encode(&sheet).expect("png"),
        )
        .expect("write");
    }
}
