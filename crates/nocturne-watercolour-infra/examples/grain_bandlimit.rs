//! Renders the `wash` and `moonlit-shoreline` artworks on the GPU with the
//! render-paper band-limit applied (the shipped path) and without it (the
//! `NOCTURNE_PAPER_BANDLIMIT=0` toggle, so both frames come from the same
//! loaded state), writing `<scene>_<long_edge>_{before,after2}.png`, and
//! prints two metrics over the painted area for each size:
//!
//! - granulation modulation: `mean |(0.5 - paper_height) * 2|` (the factor
//!   `granulation_gain` scales to modulate pigment thickness);
//! - 3x3 high-frequency residual: `mean |pixel - 3x3 mean|` per RGB channel,
//!   the per-pixel speckle measure.
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example grain_bandlimit --release -- <out_dir>
//! ```

use std::env;
use std::fs;
use std::path::PathBuf;

use nocturne_watercolour_core::application::{CpuEngine, Exporter, Playback, Renderer, Simulator};
use nocturne_watercolour_core::domain::optics::RenderParams;
use nocturne_watercolour_core::domain::paper::{PaperField, render_pixel_scale};
use nocturne_watercolour_core::domain::{Image, Palette, Scene, Seed, SimResolution};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DetailLevel};
use nocturne_watercolour_infra::export::PngExporter;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

const SEED: Seed = Seed(42);
const DURATION_MS: f32 = 4000.0;
const ALPHA_THRESHOLD: f32 = 0.02;

fn write_png(dir: &std::path::Path, name: &str, image: &Image) {
    let bytes = PngExporter.encode(image).expect("png encode");
    fs::write(dir.join(name), bytes).expect("write png");
}

/// Painted-area mask from the alpha channel of the band-limited render; the
/// same mask is used for both frames so the before/after metrics compare like
/// with like.
fn painted_mask(image: &Image) -> Vec<bool> {
    image
        .rgba
        .chunks(4)
        .map(|px| px[3] > ALPHA_THRESHOLD)
        .collect()
}

/// `mean |(0.5 - h) * 2|` of the paper height over the painted area; this is
/// the modulation `granulation_gain` scales into the pigment-thickness grain.
fn granulation_modulation(paper: &PaperField, mask: &[bool]) -> f32 {
    let mut acc = 0.0;
    let mut n = 0;
    for (painted, h) in mask.iter().zip(&paper.height) {
        if *painted {
            acc += ((0.5 - h) * 2.0).abs();
            n += 1;
        }
    }
    if n == 0 { 0.0 } else { acc / n as f32 }
}

/// `mean |pixel - 3x3 mean|` over the three RGB channels and the painted area.
fn high_freq_residual(image: &Image, mask: &[bool]) -> f32 {
    let w = image.width as usize;
    let h = image.height as usize;
    let mut acc = 0.0;
    let mut n = 0;
    for y in 0..h {
        for x in 0..w {
            let i = y * w + x;
            if !mask[i] {
                continue;
            }
            let mut sum = [0.0f64; 3];
            let mut count = 0usize;
            for dy in -1isize..=1 {
                for dx in -1isize..=1 {
                    let nx = (x as isize + dx).clamp(0, w as isize - 1) as usize;
                    let ny = (y as isize + dy).clamp(0, h as isize - 1) as usize;
                    let j = (ny * w + nx) * 4;
                    for (c, s) in sum.iter_mut().enumerate() {
                        *s += f64::from(image.rgba[j + c]);
                    }
                    count += 1;
                }
            }
            let o = i * 4;
            for (c, s) in sum.iter().enumerate() {
                acc += (f64::from(image.rgba[o + c]) - s / count as f64).abs() as f32;
            }
            n += 1;
        }
    }
    if n == 0 { 0.0 } else { acc / (n * 3) as f32 }
}

/// One scene rendered at a few long-edge output sizes; `render` toggles the
/// band-limit off for the `before` frame via the engine's env switch so both
/// frames come from the same GPU state.
fn measure(
    name: &str,
    scene: &Scene,
    sizes: &[(u32, u32)],
    ctx: &GpuContext,
    out_dir: &std::path::Path,
) {
    let mut gpu = GpuEngine::new(ctx.clone()).expect("engine");
    gpu.load(scene).expect("load");
    let mut pb = Playback::new(gpu, scene.clone(), DURATION_MS).expect("playback");
    pb.finish_immediately().expect("finish");
    let mut engine = pb.into_simulator();
    // Ceiling probe: the same state rendered on the CPU with no granulation at
    // all shows how much of the 3x3 residual is grain rather than the wash's
    // own pigment/sim structure.
    let mut cpu = CpuEngine::new(
        Default::default(),
        RenderParams {
            granulation_gain: 0.0,
            ..RenderParams::default()
        },
    );
    cpu.load(scene).expect("cpu load");
    let mut cpu_pb = Playback::new(cpu, scene.clone(), DURATION_MS).expect("cpu playback");
    cpu_pb.finish_immediately().expect("cpu finish");
    let mut cpu = cpu_pb.into_simulator();
    for &(w, h) in &sizes[1..] {
        let no_grain = cpu.render(w, h).expect("no-grain render");
        let no_grain_mask = painted_mask(&no_grain);
        let ceiling = high_freq_residual(&no_grain, &no_grain_mask);
        println!("{name} {w}x{h}: no-grain 3x3 residual ceiling {ceiling:.5}");
    }
    for &(w, h) in sizes {
        let aspect = scene.aspect();
        // SAFETY: single-threaded example; nothing else reads or writes this
        // variable while it is being toggled.
        unsafe { env::remove_var("NOCTURNE_PAPER_BANDLIMIT") };
        let after = engine.render(w, h).expect("render with band-limit");
        unsafe { env::set_var("NOCTURNE_PAPER_BANDLIMIT", "0") };
        let before = engine.render(w, h).expect("render without band-limit");
        unsafe { env::remove_var("NOCTURNE_PAPER_BANDLIMIT") };

        write_png(out_dir, &format!("{name}_{w}_after2.png"), &after);
        write_png(out_dir, &format!("{name}_{w}_before.png"), &before);

        let mask = painted_mask(&after);
        let paper_before = PaperField::generate_with_pixel_scale(&scene.paper, w, h, aspect, 0.0);
        let paper_after = PaperField::generate_with_pixel_scale(
            &scene.paper,
            w,
            h,
            aspect,
            render_pixel_scale(w, h, aspect),
        );
        let m_before = granulation_modulation(&paper_before, &mask);
        let m_after = granulation_modulation(&paper_after, &mask);
        let r_before = high_freq_residual(&before, &mask);
        let r_after = high_freq_residual(&after, &mask);
        println!(
            "{name} {w}x{h}: granulation modulation {m_before:.4} -> {m_after:.4} \
             ({:.1}%); 3x3 residual {r_before:.5} -> {r_after:.5} ({:.1}%)",
            100.0 * (m_after / m_before - 1.0),
            100.0 * (r_after / r_before - 1.0),
        );
    }
}

fn main() {
    let out_dir = env::args()
        .nth(1)
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("grain-bandlimit"));
    fs::create_dir_all(&out_dir).expect("create out dir");
    let Some(ctx) = GpuContext::try_new().expect("gpu context") else {
        eprintln!("no GPU adapter available");
        std::process::exit(2);
    };

    let mut wash = ArtworkCatalogue::build("wash", SEED, Palette::water()).expect("wash");
    wash.sim_resolution = SimResolution(256);
    measure(
        "wash",
        &wash,
        &[(256, 256), (512, 512), (1024, 1024)],
        &ctx,
        &out_dir,
    );

    let mut moonlit = ArtworkCatalogue::by_id(
        "moonlit-shoreline",
        SEED,
        &Palette::moonlight(),
        0.7,
        DetailLevel::Large,
    )
    .expect("moonlit-shoreline");
    moonlit.sim_resolution = SimResolution(256);
    measure(
        "moonlit-shoreline",
        &moonlit,
        &[(256, 144), (512, 288), (1024, 576)],
        &ctx,
        &out_dir,
    );

    println!("\nwrote PNGs to {}", out_dir.display());
}
