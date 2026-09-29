//! Renders every catalogue artwork with the GPU backend in its normal palette
//! (subtractive compositing) and its dark-surface palette (luminous
//! compositing, `Background::TransparentOnDark`), composites each over a
//! light and a dark ground, compares the normal variant with the CPU
//! reference and prints timings:
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example render_native --release -- <out_dir> [sim_resolution] [--bench]
//! ```
//!
//! `--bench` runs only the tick benchmark: GPU ms per tick with the sheet
//! wet (swirl on), the commands encoded per tick, and the optics pass alone
//! and the present path (into an offscreen canvas-format target) at a few
//! output sizes, each the median of three runs.
//!
//! An optional `sim_resolution` (e.g. 384 or 512) overrides every scene's
//! simulation grid so per-tick GPU cost can be compared across resolutions,
//! and additionally renders the `moonlit-shoreline` hero at that resolution
//! to a 900x506 PNG.
//!
//! The luminous flag travels in the simulation state header. If the GPU
//! engine's `load` does not yet forward it, the dark variants fall back to
//! the CPU reference and say so.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use nocturne_watercolour_core::application::{
    CpuEngine, Exporter, Playback, Renderer, Reveal, Simulator,
};
use nocturne_watercolour_core::domain::optics::{
    self, LUMINOUS_GRAIN_STRENGTH, LUMINOUS_TUNING, LuminousTuning, RenderParams,
};
use nocturne_watercolour_core::domain::paper::render_pixel_scale;
use nocturne_watercolour_core::domain::{
    Background, BrushStroke, CompositeMode, Image, Operation, Palette, Paper, PaperField,
    PigmentRole, Point, RadiusProfile, Rgb, Scene, SceneId, Seed, SimResolution, SizeHint,
    StrokeSpan,
};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DetailLevel};
use nocturne_watercolour_infra::document::{parse_scene_json, scene_to_json};
use nocturne_watercolour_infra::export::{FrameSequence, PngExporter, srgb_to_linear};
use nocturne_watercolour_infra::gpu::{CommandCounts, GpuContext, GpuEngine};

const SEED: Seed = Seed(42);
const REVEAL_FRAMES: u32 = 8;
const DURATION_MS: f32 = 4000.0;

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

fn palette_for(artwork: &str) -> Palette {
    match artwork {
        "crescent_moon" => Palette::moonlight(),
        "glaze_pair" => Palette::dusk(),
        _ => Palette::water(),
    }
}

/// A single moon-gold wash on the plain wash geometry, no drops: isolates the
/// compositing from any artwork's pigment mix.
fn luminous_probe_scene(seed: Seed) -> Scene {
    let palette = Palette::moonlight();
    let glow = palette.index_of(PigmentRole::Glow).unwrap_or(0);
    let mut reveal = Reveal::new(320);
    for (i, (y, water, conc)) in [
        (0.34, 1.35, 0.5),
        (0.44, 1.1, 0.55),
        (0.54, 0.95, 0.6),
        (0.64, 0.8, 0.65),
    ]
    .iter()
    .enumerate()
    {
        let wobble = if i % 2 == 0 { 0.012 } else { -0.012 };
        reveal = reveal.wash(Operation::Brush(BrushStroke {
            path: vec![
                Point::new(0.22, y + wobble),
                Point::new(0.5, y - wobble * 0.5),
                Point::new(0.78, y + wobble),
            ],
            radius: RadiusProfile::uniform(0.075),
            pigment: glow,
            concentration: *conc,
            water: *water,
            softness: 0.25,
            span: StrokeSpan::FULL,
        }));
    }
    Scene {
        id: SceneId(format!("luminous-probe-{}", seed.0)),
        size_hint: SizeHint {
            width: 512,
            height: 512,
        },
        paper: Paper::cold_press(seed),
        palette,
        timeline: reveal.settle(0.55, 3.5).build(),
        seed,
        sim_resolution: SimResolution(256),
        background: Background::TransparentOnDark,
    }
}

/// Renders the probe on the CPU at two grain strengths (full texture and the
/// shipped constant) so the damping can be judged side by side.
fn luminous_probe(out_dir: &Path, dark: Rgb) {
    let scene = luminous_probe_scene(SEED);
    let t = Instant::now();
    let mut pb = Playback::new(CpuEngine::default(), scene.clone(), DURATION_MS).expect("probe");
    pb.finish_immediately().expect("probe finish");
    let grid = pb.simulator().grid().expect("grid").clone();
    let paper_out = PaperField::generate_with_pixel_scale(
        &scene.paper,
        512,
        512,
        1.0,
        render_pixel_scale(512, 512, 1.0),
    );
    for (label, strength) in [("before", 1.0), ("after", LUMINOUS_GRAIN_STRENGTH)] {
        let image = optics::render_with_grain_strength(
            &grid,
            &scene.palette,
            &paper_out,
            &RenderParams::default(),
            strength,
        );
        write_png(out_dir, &format!("luminous_probe_{label}.png"), &image);
        write_png(
            out_dir,
            &format!("luminous_probe_{label}_over_dark.png"),
            &image.composite_over(dark),
        );
    }
    println!(
        "\n== luminous_probe (moon gold only, TransparentOnDark; before = grain strength 1.0, after = {LUMINOUS_GRAIN_STRENGTH}): {:.1} ms",
        t.elapsed().as_secs_f64() * 1000.0
    );
}

/// The catalogue `wash` on a 4:1 output, rendered on the CPU: `before` is the
/// scene as authored (square size hint) stretched to 1024x256, `after` the
/// same scene with a 1024x256 size hint, so paper grain and stamps are
/// measured in the stretched output's metric.
fn aspect_probe(out_dir: &Path, light: Rgb) {
    let t = Instant::now();
    for (label, hint) in [
        ("before", None),
        (
            "after",
            Some(SizeHint {
                width: 1024,
                height: 256,
            }),
        ),
    ] {
        let mut scene = ArtworkCatalogue::build("wash", SEED, Palette::water()).expect("wash");
        if let Some(hint) = hint {
            scene.size_hint = hint;
        }
        let mut pb = Playback::new(CpuEngine::default(), scene, DURATION_MS).expect("probe");
        pb.finish_immediately().expect("probe finish");
        let image = pb.simulator().render(1024, 256).expect("render");
        write_png(out_dir, &format!("aspect_probe_{label}.png"), &image);
        write_png(
            out_dir,
            &format!("aspect_probe_{label}_over_light.png"),
            &image.composite_over(light),
        );
    }
    println!(
        "\n== aspect_probe (wash at 1024x256; before = square size hint stretched, after = 4:1 size hint): {:.1} ms",
        t.elapsed().as_secs_f64() * 1000.0
    );
}

/// Candidate Luminous tunings rendered side by side on the CPU (the GPU only
/// knows the shipped constants), for the three luminous test artworks.
fn luminous_variants(out_dir: &Path, dark: Rgb) {
    let variants: [(&str, LuminousTuning); 2] = [
        (
            "middle",
            LuminousTuning {
                alpha_toe: 0.06,
                alpha_half: 0.5,
                alpha_max: 0.85,
                alpha_grain: 0.5,
                colour_floor: 0.5,
                colour_ceiling: 1.2,
                grain_strength: 0.7,
                chroma_gain: 1.0,
                pale_lift: 0.0,
            },
        ),
        (
            "deep",
            LuminousTuning {
                alpha_toe: 0.06,
                alpha_half: 0.5,
                alpha_max: 0.85,
                alpha_grain: 0.5,
                colour_floor: 1.0,
                colour_ceiling: 1.2,
                grain_strength: 0.85,
                chroma_gain: 1.0,
                pale_lift: 0.0,
            },
        ),
    ];
    let t = Instant::now();
    for (name, palette) in [
        ("glaze_pair", Palette::dusk()),
        ("crescent_moon", Palette::moonlight()),
        ("wash", Palette::water()),
    ] {
        let mut scene = ArtworkCatalogue::build(name, SEED, palette).expect("artwork");
        scene.background = Background::TransparentOnDark;
        let mut pb = Playback::new(CpuEngine::default(), scene.clone(), DURATION_MS).expect("pb");
        pb.finish_immediately().expect("finish");
        let grid = pb.simulator().grid().expect("grid").clone();
        let paper_out = PaperField::generate_with_pixel_scale(
            &scene.paper,
            512,
            512,
            scene.aspect(),
            render_pixel_scale(512, 512, scene.aspect()),
        );
        for (suffix, tuning) in &variants {
            let image = optics::render_with_luminous_tuning(
                &grid,
                &scene.palette,
                &paper_out,
                &RenderParams::default(),
                tuning,
            );
            write_png(
                out_dir,
                &format!("{name}_luminous_over_dark_{suffix}.png"),
                &image.composite_over(dark),
            );
        }
    }
    println!(
        "\n== luminous_variants (shipped {LUMINOUS_TUNING:?}; middle/deep as in the example source): {:.1} ms",
        t.elapsed().as_secs_f64() * 1000.0
    );
}

fn finished_frame<E: Simulator + Renderer>(engine: E, scene: &Scene) -> (E, Image, f64) {
    let t = Instant::now();
    let mut pb = Playback::new(engine, scene.clone(), DURATION_MS).expect("playback");
    pb.finish_immediately().expect("finish");
    let run_ms = t.elapsed().as_secs_f64() * 1000.0;
    let image = pb.simulator().render(512, 512).expect("render");
    (pb.into_simulator(), image, run_ms)
}

/// The former showcase hero at a given simulation resolution, to 900x506: the
/// coordinator compares the edge quality across resolutions. `size_hint` is
/// set to the output aspect so paper grain and stamps are measured in it.
fn shoreline_wide(gpu: GpuEngine, out_dir: &Path, resolution: u32, light: Rgb) -> GpuEngine {
    let mut scene = ArtworkCatalogue::by_id(
        "moonlit-shoreline",
        SEED,
        &Palette::moonlight(),
        0.7,
        DetailLevel::Large,
    )
    .expect("moonlit-shoreline");
    scene.size_hint = SizeHint {
        width: 900,
        height: 506,
    };
    scene.sim_resolution = SimResolution(resolution);
    let total_ticks = scene.timeline.total_ticks;
    println!(
        "\n== moonlit-shoreline_wide ({}x{} sim, {} ticks, {}x{} out)",
        resolution, resolution, total_ticks, scene.size_hint.width, scene.size_hint.height
    );
    let mut gpu = gpu;
    gpu.load(&scene).expect("load");
    gpu.sync().expect("sync");
    let t = Instant::now();
    let mut pb = Playback::new(gpu, scene.clone(), DURATION_MS).expect("playback");
    pb.finish_immediately().expect("finish");
    pb.simulator().sync().expect("sync");
    let run = t.elapsed();
    println!(
        "gpu straight run: {:.1} ms total, {:.3} ms/tick over {} ticks",
        run.as_secs_f64() * 1000.0,
        run.as_secs_f64() * 1000.0 / total_ticks as f64,
        total_ticks
    );
    let t = Instant::now();
    let image = pb
        .simulator()
        .render(scene.size_hint.width, scene.size_hint.height)
        .expect("render 900x506");
    println!(
        "gpu render 900x506 incl. readback: {:.1} ms",
        t.elapsed().as_secs_f64() * 1000.0
    );
    let tag = format!("moonlit-shoreline_{resolution}");
    write_png(out_dir, &format!("{tag}.png"), &image);
    write_png(
        out_dir,
        &format!("{tag}_over_light.png"),
        &image.composite_over(light),
    );
    pb.into_simulator()
}

/// FNV-1a over `u32` words: a digest to confirm a change is bit-exact.
fn fnv(words: impl Iterator<Item = u32>) -> u64 {
    words.fold(0xcbf2_9ce4_8422_2325, |h, w| {
        (h ^ u64::from(w)).wrapping_mul(0x0100_0000_01b3)
    })
}

fn median(mut samples: Vec<f64>) -> f64 {
    samples.sort_by(f64::total_cmp);
    samples[samples.len() / 2]
}

fn per_tick(before: CommandCounts, after: CommandCounts, ticks: u32) -> String {
    let t = u64::from(ticks);
    format!(
        "{:.2} passes, {:.2} dispatches, {:.2} copies, {:.3} submits",
        (after.passes - before.passes) as f64 / t as f64,
        (after.dispatches - before.dispatches) as f64 / t as f64,
        (after.copies - before.copies) as f64 / t as f64,
        (after.submits - before.submits) as f64 / t as f64,
    )
}

/// CPU cost of reading a catalogue scene document, as `createInstance` does.
fn json_bench() {
    let scene = ArtworkCatalogue::build("glaze_pair", SEED, Palette::dusk()).expect("scene");
    let json = scene_to_json(&scene).expect("json");
    let mut ms = Vec::new();
    for _ in 0..3 {
        let t = Instant::now();
        for _ in 0..20 {
            parse_scene_json(&json).expect("parse");
        }
        ms.push(t.elapsed().as_secs_f64() * 1000.0 / 20.0);
    }
    println!(
        "bench scene json ({} KB): {:.3} ms/parse median (runs {:?})",
        json.len() / 1024,
        median(ms.clone()),
        ms.iter()
            .map(|v| (v * 1e3).round() / 1e3)
            .collect::<Vec<_>>()
    );
}

/// CPU cost of the render-resolution paper a first present generates.
fn paper_bench() {
    let paper = Paper::cold_press(SEED);
    for size in [256u32, 512, 1024] {
        let mut ms = Vec::new();
        let mut digest = 0;
        for _ in 0..3 {
            let t = Instant::now();
            let field = PaperField::generate_with_pixel_scale(
                &paper,
                size,
                size,
                1.0,
                render_pixel_scale(size, size, 1.0),
            );
            ms.push(t.elapsed().as_secs_f64() * 1000.0);
            digest = fnv(field.height.iter().map(|f| f.to_bits()));
        }
        println!(
            "bench paper {size}x{size}: {:.2} ms median (runs {:?}), digest {digest:016x}",
            median(ms.clone()),
            ms.iter()
                .map(|v| (v * 100.0).round() / 100.0)
                .collect::<Vec<_>>()
        );
    }
}

/// GPU cost of the tick loop on a wet sheet: the catalogue `wash` played to
/// 30% (every stroke down, swirl active), then `BENCH_TICKS` bare ticks per
/// run.
fn tick_bench(gpu: GpuEngine, resolution: u32) -> GpuEngine {
    const BENCH_TICKS: u32 = 192;
    const RUNS: usize = 3;
    let mut scene = ArtworkCatalogue::build("wash", SEED, Palette::water()).expect("wash");
    scene.sim_resolution = SimResolution(resolution);
    let mut pb = Playback::new(gpu, scene.clone(), DURATION_MS).expect("playback");
    let warm = pb.total_ticks() * 3 / 10;
    pb.advance_ticks(warm).expect("advance");
    pb.simulator().sync().expect("sync");
    let gpu = pb.simulator();
    let mut tick_ms = Vec::new();
    let mut stamped = Vec::new();
    let mut counts = String::new();
    for _ in 0..RUNS {
        let before = gpu.command_counts();
        let t = Instant::now();
        gpu.step(BENCH_TICKS).expect("step");
        gpu.sync().expect("sync");
        tick_ms.push(t.elapsed().as_secs_f64() * 1000.0 / f64::from(BENCH_TICKS));
        counts = per_tick(before, gpu.command_counts(), BENCH_TICKS);
        stamped.extend(gpu.gpu_timings().tick_ms);
    }
    println!(
        "bench {resolution}^2 sim: {:.4} ms/tick median of {RUNS} (runs {:?}); per tick: {counts}",
        median(tick_ms.clone()),
        tick_ms
            .iter()
            .map(|v| (v * 1e4).round() / 1e4)
            .collect::<Vec<_>>()
    );
    if !stamped.is_empty() {
        println!(
            "bench {resolution}^2 sim: timestamps {:.4} ms/tick median",
            median(stamped)
        );
    }
    let grid = gpu.read_grid().expect("read grid");
    let frame = gpu.render(256, 256).expect("render");
    let state_bits = [
        &grid.wet,
        &grid.velocity_u,
        &grid.velocity_v,
        &grid.pressure,
        &grid.saturation,
        &grid.pigments_in_water,
        &grid.pigments_deposited,
    ]
    .into_iter()
    .flat_map(|v| v.iter().map(|f| f.to_bits()));
    println!(
        "bench {resolution}^2 sim: digest state {:016x} frame256 {:016x}",
        fnv(state_bits.chain([grid.tick])),
        fnv(frame.rgba.iter().map(|f| f.to_bits()))
    );
    for size in [256u32, 512, 1024] {
        gpu.render_without_readback(size, size).expect("render");
        gpu.sync().expect("sync");
        let mut ms = Vec::new();
        for _ in 0..RUNS {
            let t = Instant::now();
            for _ in 0..8 {
                gpu.render_without_readback(size, size).expect("render");
            }
            gpu.sync().expect("sync");
            ms.push(t.elapsed().as_secs_f64() * 1000.0 / 8.0);
        }
        if let Some(stamp) = gpu.gpu_timings().render_ms {
            println!("bench {resolution}^2 sim: timestamps optics {size}x{size} {stamp:.3} ms");
        }
        println!(
            "bench {resolution}^2 sim: optics {size}x{size} {:.3} ms/frame median (runs {:?})",
            median(ms.clone()),
            ms.iter()
                .map(|v| (v * 1e3).round() / 1e3)
                .collect::<Vec<_>>()
        );
        gpu.present_offscreen(size, size, false).expect("present");
        gpu.sync().expect("sync");
        let mut ms = Vec::new();
        for _ in 0..RUNS {
            let t = Instant::now();
            for _ in 0..8 {
                gpu.present_offscreen(size, size, false).expect("present");
            }
            gpu.sync().expect("sync");
            ms.push(t.elapsed().as_secs_f64() * 1000.0 / 8.0);
        }
        if let Some(stamp) = gpu.gpu_timings().render_ms {
            println!("bench {resolution}^2 sim: timestamps present {size}x{size} {stamp:.3} ms");
        }
        println!(
            "bench {resolution}^2 sim: present {size}x{size} rgba8 {:.3} ms/frame median (runs {:?})",
            median(ms.clone()),
            ms.iter()
                .map(|v| (v * 1e3).round() / 1e3)
                .collect::<Vec<_>>()
        );
    }
    pb.into_simulator()
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let bench = args.iter().any(|a| a == "--bench");
    let mut positional = args.iter().filter(|a| !a.starts_with("--"));
    let out_dir: PathBuf = positional
        .next()
        .map(PathBuf::from)
        .expect("usage: render_native <out_dir> [sim_resolution] [--bench]");
    let sim_resolution: Option<u32> = positional
        .next()
        .map(|a| a.parse().expect("sim_resolution must be an integer"));
    fs::create_dir_all(&out_dir).expect("create out dir");

    let t0 = Instant::now();
    let ctx = match GpuContext::try_new().expect("gpu context") {
        Some(ctx) => ctx,
        None => {
            eprintln!("no GPU adapter available");
            std::process::exit(2);
        }
    };
    let mut gpu = GpuEngine::new(ctx.clone()).expect("gpu engine");
    println!(
        "device init: {:.1} ms ({} via {:?})",
        t0.elapsed().as_secs_f64() * 1000.0,
        ctx.adapter_name(),
        ctx.backend()
    );

    if bench {
        paper_bench();
        json_bench();
        for res in sim_resolution.map_or(vec![160, 256, 384], |r| vec![r]) {
            gpu = tick_bench(gpu, res);
        }
        return;
    }

    let light = hex(0xf7f5f0);
    let dark = hex(0x0f1420);

    luminous_probe(&out_dir, dark);
    aspect_probe(&out_dir, light);
    luminous_variants(&out_dir, dark);

    for entry in ArtworkCatalogue::entries() {
        let base_palette = palette_for(entry.name);

        // Normal variant: GPU, subtractive, plus reveal frames and the CPU comparison.
        let mut scene: Scene = (entry.build)(SEED, base_palette.clone());
        if let Some(res) = sim_resolution {
            scene.sim_resolution = SimResolution(res);
        }
        let total_ticks = scene.timeline.total_ticks;
        println!(
            "\n== {}_normal ({}x{} sim, {} ticks, palette {}, {:?})",
            entry.name,
            scene.sim_resolution.0,
            scene.sim_resolution.0,
            total_ticks,
            scene.palette.name,
            scene.composite_mode()
        );
        gpu.load(&scene).expect("load");
        gpu.sync().expect("sync");
        let t = Instant::now();
        let mut pb = Playback::new(gpu, scene.clone(), DURATION_MS).expect("playback");
        pb.finish_immediately().expect("finish");
        pb.simulator().sync().expect("sync");
        let run = t.elapsed();
        println!(
            "gpu straight run: {:.1} ms total, {:.3} ms/tick averaged over {} ticks (incl. strokes and checkpoints)",
            run.as_secs_f64() * 1000.0,
            run.as_secs_f64() * 1000.0 / total_ticks as f64,
            total_ticks
        );
        let t = Instant::now();
        let finished = pb.simulator().render(512, 512).expect("render");
        println!(
            "gpu render 512x512 incl. readback: {:.1} ms",
            t.elapsed().as_secs_f64() * 1000.0
        );
        let tag = format!("{}_normal", entry.name);
        write_png(&out_dir, &format!("{tag}_gpu.png"), &finished);
        write_png(
            &out_dir,
            &format!("{tag}_gpu_over_light.png"),
            &finished.composite_over(light),
        );
        write_png(
            &out_dir,
            &format!("{tag}_gpu_over_dark.png"),
            &finished.composite_over(dark),
        );

        let t = Instant::now();
        let frames = FrameSequence {
            count: REVEAL_FRAMES,
            width: 256,
            height: 256,
        }
        .render(&mut pb)
        .expect("frames");
        println!(
            "gpu {} reveal frames 256x256 (seek + replay + render): {:.1} ms",
            REVEAL_FRAMES,
            t.elapsed().as_secs_f64() * 1000.0
        );
        for (i, f) in frames.iter().enumerate() {
            write_png(&out_dir, &format!("{}_reveal_{i:02}.png", entry.name), f);
        }
        gpu = pb.into_simulator();

        let (_, cpu_image, cpu_run_ms) = finished_frame(CpuEngine::default(), &scene);
        println!(
            "cpu straight run: {cpu_run_ms:.1} ms ({:.3} ms/tick)",
            cpu_run_ms / total_ticks as f64
        );
        write_png(&out_dir, &format!("{}_cpu.png", entry.name), &cpu_image);
        let mae = finished.mean_abs_diff(&cpu_image).expect("same size");
        let max_diff = finished
            .rgba
            .iter()
            .zip(&cpu_image.rgba)
            .map(|(a, b)| (a - b).abs())
            .fold(0.0f32, f32::max);
        println!(
            "cpu vs gpu finished frame: mean abs diff {mae:.6}, max abs diff {max_diff:.4} (linear premultiplied RGBA, 0..1)"
        );

        // Luminous compositing for dark hosts, with the normal palette (the
        // recommended pairing) and with the dark-surface palette for comparison.
        for (variant, palette) in [
            ("luminous", base_palette.clone()),
            ("dark_luminous", base_palette.for_dark_surface()),
        ] {
            let mut dark_scene: Scene = (entry.build)(SEED, palette);
            dark_scene.background = Background::TransparentOnDark;
            println!(
                "\n== {}_{variant} (palette {}, {:?})",
                entry.name,
                dark_scene.palette.name,
                dark_scene.composite_mode()
            );
            gpu.load(&dark_scene).expect("load");
            let forwarded =
                gpu.read_grid().expect("read grid").composite_mode == CompositeMode::Luminous;
            let (dark_image, route) = if forwarded {
                let (g, image, run_ms) = finished_frame(gpu, &dark_scene);
                gpu = g;
                gpu.sync().expect("sync");
                (image, format!("gpu ({run_ms:.1} ms run)"))
            } else {
                let (_, image, run_ms) = finished_frame(CpuEngine::default(), &dark_scene);
                (
                    image,
                    format!(
                        "cpu fallback ({run_ms:.1} ms run): GpuEngine::load does not forward the composite mode into the state header yet"
                    ),
                )
            };
            println!("rendered via {route}");
            let tag = format!("{}_{variant}", entry.name);
            write_png(&out_dir, &format!("{tag}.png"), &dark_image);
            write_png(
                &out_dir,
                &format!("{tag}_over_dark.png"),
                &dark_image.composite_over(dark),
            );
            write_png(
                &out_dir,
                &format!("{tag}_over_light.png"),
                &dark_image.composite_over(light),
            );
        }
    }
    if let Some(res) = sim_resolution {
        shoreline_wide(gpu, &out_dir, res, light);
    }
    println!("\nwrote PNGs to {}", out_dir.display());
}
