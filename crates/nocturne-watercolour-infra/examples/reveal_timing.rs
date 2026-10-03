//! Reveal timing: renders N frames evenly spaced in wall-clock progress for
//! one catalogue artwork (or every id with `all`) and prints the numbers the
//! reveal is designed around, so "the pen moves, the ink blooms, the artwork
//! sets" is checkable rather than squinted at:
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example reveal_timing --release -- <out_dir> [id] [palette] [frames] [flags...]
//! ```
//!
//! `FrameSequence` seeks by progress, so it follows the playback's `Reveal`
//! curve: the frames land in the viewer's time (the paint phase in the first
//! ~600 ms, the tail over the rest) instead of the simulation's. For each
//! frame the example prints the wall-clock millisecond, the simulation tick,
//! the mean absolute difference from the previous frame, the covered area
//! (alpha > 0.02) and the alpha-weighted centroid.
//!
//! The verdict measures the reveal against its own paint/tail split (read off
//! the playback's curve, not hardcoded): three paint checkpoints sample the
//! covered area at 25/50/100 % of the paint wall-clock by seeking to those
//! progresses directly (so they do not depend on the frame count), the tail
//! reports its minimum frame-to-frame MAE and whether the changes decay
//! monotonically (a stall that then jumps is as wrong as a dead tail), and
//! `--reference <dir>` compares the final frame against a previously written
//! `frame-NN.png` of the same size so a parameter change that quietly
//! repaints the artwork is caught.
//!
//! Flags override the choreography for that run only (the scene is built
//! unchoreographed through `ArtworkCatalogue::by_id_for_unchoreographed` and
//! re-choreographed with the overridden parameters):
//!
//! ```text
//! --paint-spread <f> --stroke-overlap <f> --max-steps <n>
//! --settle-fraction <f> --settle-budget <f>
//! --wet-darken <f> --wet-sheen-add <f> --sheen-depth <f> --reference <dir>
//! ```
//!
//! Output to `<out_dir>`: `frame-00.png`..`frame-NN.png` (straight-alpha
//! sRGB), `strip.png` (every frame composited over a light ground in a
//! 4-column grid, each cell 256 px on the artwork's long side), and
//! `strip-dark.png` (a second run of the same artwork built with
//! `Background::TransparentOnDark`, over a dark ground).

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use nocturne_watercolour_core::application::playback::DEFAULT_PAINT_WALL_FRACTION;
use nocturne_watercolour_core::application::{
    CpuEngine, EngineError, Exporter, Playback, ProgressCurve, Renderer, Simulator,
};
use nocturne_watercolour_core::domain::optics::RenderParams;
use nocturne_watercolour_core::domain::sim::SimParams;
use nocturne_watercolour_core::domain::{Background, Image, Operation, Palette, Rgb, Scene, Seed};
use nocturne_watercolour_infra::authoring::{
    ArtworkCatalogue, Choreography, DEFAULT_INTENSITY, DetailLevel, SETTLE_TICK_FRACTION,
    choreograph_scene_with,
};
use nocturne_watercolour_infra::export::{FrameSequence, PngExporter, srgb_to_linear};
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

const SEED: Seed = Seed(42);
const DURATION_MS: f32 = 3000.0;
const LONG_SIDE: u32 = 512;
const STRIP_LONG: u32 = 256;
const GUTTER: u32 = 8;
const GRID_COLUMNS: u32 = 4;
const AREA_THRESHOLD: f32 = 0.02;
const TAIL_FLOOR: f32 = 0.0005;
/// The tail's frame-to-frame MAE may not rise by more than this factor from
/// one pair to the next: a tail that stalls and then jumps is as wrong as a
/// dead one.
const MONOTONE_JUMP: f32 = 1.5;
/// Final-frame MAE against a `--reference` run that still counts as the same
/// artwork.
const REFERENCE_WANT: f32 = 0.02;

/// Paint checkpoints as fractions of the paint wall-clock, and the covered
/// area, as a share of the final area, that the reveal is designed around:
/// at 25 % and 50 % the mark must still be arriving, and at 100 % it must be
/// nearly all there.
const PAINT_25: f32 = 0.25;
const PAINT_50: f32 = 0.50;
const PAINT_100: f32 = 1.00;

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

/// Output size with the scene's aspect and `long` on its longer side.
fn output_size(scene: &Scene, long: u32) -> (u32, u32) {
    let (w, h) = (scene.size_hint.width as f32, scene.size_hint.height as f32);
    if w >= h {
        (long, ((long as f32 * h / w).round() as u32).max(1))
    } else {
        (((long as f32 * w / h).round() as u32).max(1), long)
    }
}

/// Bilinear resample keeping `long` on the image's long side.
fn resize_long(image: &Image, long: u32) -> Image {
    let sw = image.width as f32;
    let sh = image.height as f32;
    let (nw, nh) = if sw >= sh {
        (long, ((long as f32 * sh / sw).round() as u32).max(1))
    } else {
        (((long as f32 * sw / sh).round() as u32).max(1), long)
    };
    let mut out = Image::new(nw, nh);
    let (w, h) = (nw as f32, nh as f32);
    for y in 0..nh {
        let sy = (y as f32 + 0.5) * sh / h - 0.5;
        let y0 = sy.floor().clamp(0.0, (sh - 1.0).max(0.0)) as usize;
        let y1 = sy.ceil().clamp(0.0, (sh - 1.0).max(0.0)) as usize;
        let fy = sy - y0 as f32;
        for x in 0..nw {
            let sx = (x as f32 + 0.5) * sw / w - 0.5;
            let x0 = sx.floor().clamp(0.0, (sw - 1.0).max(0.0)) as usize;
            let x1 = sx.ceil().clamp(0.0, (sw - 1.0).max(0.0)) as usize;
            let fx = sx - x0 as f32;
            let o = (y * nw + x) as usize * 4;
            for c in 0..4 {
                let src =
                    |px: usize, py: usize| image.rgba[(py * image.width as usize + px) * 4 + c];
                let top = src(x0, y0) + (src(x1, y0) - src(x0, y0)) * fx;
                let bot = src(x0, y1) + (src(x1, y1) - src(x0, y1)) * fx;
                out.rgba[o + c] = top + (bot - top) * fy;
            }
        }
    }
    out
}

/// Every frame scaled to [`STRIP_LONG`] on its long side, composited over an
/// opaque `ground` in a [`GRID_COLUMNS`]-wide grid with [`GUTTER`] px gaps,
/// one cell per frame in reading order.
fn make_sheet(frames: &[Image], ground: Rgb) -> Image {
    let cells: Vec<Image> = frames.iter().map(|f| resize_long(f, STRIP_LONG)).collect();
    let cell_w = cells[0].width;
    let cell_h = cells[0].height;
    let rows = (cells.len() as u32).div_ceil(GRID_COLUMNS).max(1);
    let mut sheet = Image::new(
        GRID_COLUMNS * cell_w + GUTTER * (GRID_COLUMNS - 1),
        rows * cell_h + GUTTER * (rows - 1),
    );
    for px in sheet.rgba.chunks_exact_mut(4) {
        px.copy_from_slice(&[ground.0[0], ground.0[1], ground.0[2], 1.0]);
    }
    for (i, cell) in cells.iter().enumerate() {
        let ox = (i as u32 % GRID_COLUMNS) * (cell_w + GUTTER);
        let oy = (i as u32 / GRID_COLUMNS) * (cell_h + GUTTER);
        for y in 0..cell_h {
            for x in 0..cell_w {
                let src = ((y * cell_w + x) as usize) * 4;
                let dst = (((oy + y) * sheet.width + ox + x) as usize) * 4;
                let a = cell.rgba[src + 3];
                for c in 0..3 {
                    sheet.rgba[dst + c] = cell.rgba[src + c] + sheet.rgba[dst + c] * (1.0 - a);
                }
                sheet.rgba[dst + 3] = 1.0;
            }
        }
    }
    sheet
}

/// Fraction of pixels with alpha above [`AREA_THRESHOLD`].
fn area(image: &Image) -> f32 {
    let n = image
        .rgba
        .chunks_exact(4)
        .filter(|px| px[3] > AREA_THRESHOLD)
        .count();
    n as f32 / (image.width * image.height) as f32
}

/// Wall-clock progress of frame `i` of `count`, matching `FrameSequence`.
fn frame_progress(i: u32, count: u32) -> f32 {
    if count <= 1 {
        1.0
    } else {
        i as f32 / (count - 1) as f32
    }
}

/// Alpha-weighted centroid in 0..1; `None` when nothing has alpha.
fn centroid(image: &Image) -> Option<(f32, f32)> {
    let mut sum = 0.0f64;
    let (mut sx, mut sy) = (0.0f64, 0.0f64);
    for y in 0..image.height {
        for x in 0..image.width {
            let a = f64::from(image.rgba[((y * image.width + x) * 4 + 3) as usize]);
            if a > 0.0 {
                sum += a;
                sx += f64::from(x) * a;
                sy += f64::from(y) * a;
            }
        }
    }
    if sum <= 0.0 {
        return None;
    }
    let (w, h) = (
        f64::from(image.width - 1).max(1.0),
        f64::from(image.height - 1).max(1.0),
    );
    Some(((sx / sum / w) as f32, (sy / sum / h) as f32))
}

/// A scene run: the evenly spaced frame sequence, the simulation tick each
/// frame's wall-clock progress lands on (read off the playback's own curve),
/// and the paint checkpoints rendered by seeking to the paint wall-clock
/// progresses directly.
struct Measurements {
    frames: Vec<Image>,
    ticks: Vec<u32>,
    /// `(paint fraction, wall-clock ms, image)` at 25/50/100 % of paint.
    checkpoints: Vec<(f32, f32, Image)>,
}

/// Renders `count` frames of `scene` at `width` x `height` and the three
/// paint checkpoints; returns the engine and the measurements.
fn render_measurements<E: Simulator + Renderer>(
    engine: E,
    scene: &Scene,
    count: u32,
    width: u32,
    height: u32,
) -> Result<(E, Measurements), EngineError> {
    let mut pb = Playback::new(engine, scene.clone(), DURATION_MS)?;
    let ticks: Vec<u32> = (0..count)
        .map(|i| pb.tick_for_progress(frame_progress(i, count)))
        .collect();
    let frames = FrameSequence {
        count,
        width,
        height,
    }
    .render(&mut pb)?;
    let ProgressCurve::Reveal { wall_split, .. } =
        ProgressCurve::reveal_for(scene, DEFAULT_PAINT_WALL_FRACTION)
    else {
        unreachable!("reveal_for always returns Reveal")
    };
    let mut checkpoints = Vec::new();
    for frac in [PAINT_25, PAINT_50, PAINT_100] {
        let progress = frac * wall_split;
        pb.seek_progress(progress)?;
        checkpoints.push((
            frac,
            progress * DURATION_MS,
            pb.simulator().render(width, height)?,
        ));
    }
    Ok((
        pb.into_simulator(),
        Measurements {
            frames,
            ticks,
            checkpoints,
        },
    ))
}

/// Runs `scene` on the GPU when it forwards the scene's composite mode, and
/// on the CPU reference otherwise (or when no adapter exists); returns the
/// measurements and which backend produced them. Both backends render with
/// `render_params` (the `--wet-darken` override, or the defaults).
fn run_scene(
    gpu: &mut Option<GpuEngine>,
    scene: &Scene,
    count: u32,
    width: u32,
    height: u32,
    render_params: RenderParams,
) -> (Measurements, &'static str) {
    if let Some(g) = gpu.as_mut() {
        g.load(scene).expect("gpu load");
        let forwarded = g.read_grid().expect("read grid").composite_mode == scene.composite_mode();
        if forwarded {
            let (engine, meas) =
                render_measurements(gpu.take().expect("gpu"), scene, count, width, height)
                    .expect("sequence");
            *gpu = Some(engine);
            return (meas, "gpu");
        }
        println!(
            "GpuEngine::load does not forward {:?} into the state header; using the CPU reference",
            scene.composite_mode()
        );
    }
    let (_, meas) = render_measurements(
        CpuEngine::new(SimParams::default(), render_params),
        scene,
        count,
        width,
        height,
    )
    .expect("sequence");
    (meas, "cpu")
}

fn print_table(frames: &[Image], ticks: &[u32]) {
    let count = frames.len() as u32;
    println!(
        "{:>5}  {:>7}  {:>5}  {:>7}  {:>7}  {:>7}  {:>7}",
        "frame", "wall_ms", "tick", "mae", "area", "cx", "cy"
    );
    for (i, f) in frames.iter().enumerate() {
        let wall_ms = frame_progress(i as u32, count) * DURATION_MS;
        let mae = if i == 0 {
            "-".to_string()
        } else {
            format!("{:.4}", f.mean_abs_diff(&frames[i - 1]).expect("same size"))
        };
        let (cx, cy) = centroid(f)
            .map(|(x, y)| (format!("{x:.3}"), format!("{y:.3}")))
            .unwrap_or_else(|| ("-".to_string(), "-".to_string()));
        println!(
            "{:>5}  {:>7.0}  {:>5}  {:>7}  {:>7.3}  {:>7}  {:>7}",
            i,
            wall_ms,
            ticks[i],
            mae,
            area(f),
            cx,
            cy
        );
    }
}

/// The tail's consecutive frame-to-frame MAE values (including the boundary
/// pair from the last paint frame), in frame order.
fn tail_mae(meas: &Measurements, wall_split: f32) -> Vec<f32> {
    let count = meas.frames.len() as u32;
    let mut out = Vec::new();
    for i in 1..count {
        if frame_progress(i, count) > wall_split {
            out.push(
                meas.frames[i as usize]
                    .mean_abs_diff(&meas.frames[i as usize - 1])
                    .expect("same size"),
            );
        }
    }
    out
}

/// Decodes `dir/frame-NN.png` (straight-alpha sRGB, as [`PngExporter`]
/// writes) back into the premultiplied-linear [`Image`] the renderer
/// produces, so it can be compared on equal terms.
fn read_reference_final(dir: &Path, count: u32) -> Option<Result<Image, String>> {
    let bytes = fs::read(dir.join(format!("frame-{:02}.png", count - 1))).ok()?;
    let decoder = png::Decoder::new(std::io::Cursor::new(bytes));
    let mut reader = match decoder.read_info() {
        Ok(r) => r,
        Err(e) => return Some(Err(format!("decode: {e}"))),
    };
    let mut buf = vec![0; reader.output_buffer_size().unwrap_or(0)];
    let info = match reader.next_frame(&mut buf) {
        Ok(info) => info,
        Err(e) => return Some(Err(format!("decode: {e}"))),
    };
    if info.color_type != png::ColorType::Rgba || info.bit_depth != png::BitDepth::Eight {
        return Some(Err(format!(
            "expected 8-bit RGBA, got {:?} {:?}",
            info.color_type, info.bit_depth
        )));
    }
    let mut image = Image::new(info.width, info.height);
    for (dst, px) in image.rgba.chunks_exact_mut(4).zip(buf.chunks_exact(4)) {
        let a = px[3] as f32 / 255.0;
        for (c, p) in px[..3].iter().enumerate() {
            dst[c] = srgb_to_linear(*p as f32 / 255.0) * a;
        }
        dst[3] = a;
    }
    Some(Ok(image))
}

fn print_verdict(meas: &Measurements, scene: &Scene, reference: Option<&Path>) {
    let last = meas.frames.len() - 1;
    let final_area = area(&meas.frames[last]);
    let ProgressCurve::Reveal { wall_split, .. } =
        ProgressCurve::reveal_for(scene, DEFAULT_PAINT_WALL_FRACTION)
    else {
        unreachable!("reveal_for always returns Reveal")
    };

    let wants = [
        (PAINT_25, 0.45f32, false),
        (PAINT_50, 0.75, false),
        (PAINT_100, 0.90, true),
    ];
    let mut paced = true;
    for ((frac, wall_ms, image), (_, want, lower)) in meas.checkpoints.iter().zip(&wants) {
        let share = if final_area > 0.0 {
            area(image) / final_area
        } else {
            0.0
        };
        let met = if *lower {
            share >= *want
        } else {
            share <= *want
        };
        paced &= met;
        println!(
            "paint    {:>3} %  {:>3.0} ms   area {:.2} of final    (want {} {want:.2})",
            (frac * 100.0) as u32,
            wall_ms,
            share,
            if *lower { ">=" } else { "<=" },
        );
    }

    let tail = tail_mae(meas, wall_split);
    let min_mae = tail.iter().copied().fold(f32::INFINITY, f32::min);
    let min_mae = if tail.is_empty() { 0.0 } else { min_mae };
    let last_pair = tail.last().copied().unwrap_or(0.0);
    let monotone = tail.windows(2).all(|w| w[1] <= w[0] * MONOTONE_JUMP);
    let tail_alive = min_mae > TAIL_FLOOR;
    println!(
        "tail     mae   min {min_mae:.4} (want > {TAIL_FLOOR:.4})   last pair {last_pair:.4}   monotone decay: {}",
        if monotone { "yes" } else { "no" }
    );

    let preserved = match reference {
        Some(dir) => {
            let frames = meas.frames.len() as u32;
            match read_reference_final(dir, frames) {
                None => {
                    println!(
                        "final    area {final_area:.3}   vs reference {}: missing frame-{:02}.png",
                        dir.display(),
                        frames - 1
                    );
                    None
                }
                Some(Err(e)) => {
                    println!(
                        "final    area {final_area:.3}   vs reference {}: {e}",
                        dir.display()
                    );
                    None
                }
                Some(Ok(ref_image)) => {
                    if ref_image.width != meas.frames[last].width
                        || ref_image.height != meas.frames[last].height
                    {
                        println!(
                            "final    area {final_area:.3}   vs reference {}: size mismatch (ref {}x{}, run {}x{})",
                            dir.display(),
                            ref_image.width,
                            ref_image.height,
                            meas.frames[last].width,
                            meas.frames[last].height
                        );
                        None
                    } else {
                        let diff = meas.frames[last]
                            .mean_abs_diff(&ref_image)
                            .expect("same size");
                        println!(
                            "final    area {final_area:.3}   vs reference {}: mae {diff:.4} (want < {REFERENCE_WANT:.2})",
                            dir.display()
                        );
                        Some(diff < REFERENCE_WANT)
                    }
                }
            }
        }
        None => {
            println!("final    area {final_area:.3}   no reference");
            None
        }
    };

    let preserved = match preserved {
        Some(ok) => {
            if ok {
                "yes"
            } else {
                "no"
            }
        }
        None => "n/a",
    };
    println!(
        "VERDICT  paced: {}    tail alive: {}    final preserved: {}",
        if paced { "yes" } else { "no" },
        if tail_alive { "yes" } else { "no" },
        preserved
    );
}

/// Choreography and settle overrides for one run, from the command-line
/// flags; `None` keeps the authoring default.
#[derive(Debug, Default)]
struct Config {
    paint_spread: Option<f32>,
    stroke_overlap: Option<f32>,
    max_steps: Option<u32>,
    settle_fraction: Option<f32>,
    settle_budget: Option<f32>,
    wet_darken: Option<f32>,
    wet_sheen_add: Option<f32>,
    sheen_depth: Option<f32>,
    reference: Option<PathBuf>,
}

/// Parsed command line: the sweep overrides plus the positionals.
#[derive(Debug)]
struct Cli {
    config: Config,
    out_dir: PathBuf,
    id: String,
    palette_name: String,
    count: u32,
}

impl Config {
    /// Builds the scene unchoreographed through the catalogue, then applies
    /// the overridden choreography and settle placement; `--settle-budget`
    /// re-rates the settle as [`settle_rate_for_ticks`] would with a budget
    /// other than `SETTLE_TICK_BUDGET`.
    fn scene_for(&self, id: &str, palette: &Palette, background: Background) -> Scene {
        let mut scene = ArtworkCatalogue::by_id_for_unchoreographed(
            id,
            SEED,
            palette,
            DEFAULT_INTENSITY,
            DetailLevel::Large,
            background,
            None,
        )
        .expect("catalogue id");
        let mut choro = Choreography::default();
        if let Some(v) = self.paint_spread {
            choro.paint_spread = v;
        }
        if let Some(v) = self.stroke_overlap {
            choro.stroke_overlap = v;
        }
        if let Some(v) = self.max_steps {
            choro.max_steps = v;
        }
        let settle = self.settle_fraction.unwrap_or(SETTLE_TICK_FRACTION);
        choreograph_scene_with(&mut scene, &choro, settle);
        if let Some(budget) = self.settle_budget {
            let total = scene.timeline.total_ticks;
            if let Some(ev) = scene
                .timeline
                .events
                .iter_mut()
                .rev()
                .find(|e| matches!(e.op, Operation::Dry { .. }))
            {
                let tail = total.saturating_sub(ev.at_tick);
                if let Operation::Dry { rate } = &mut ev.op {
                    *rate = (budget / tail.max(1) as f32).clamp(0.5, 64.0);
                }
            }
        }
        scene
    }
}

/// Parses `<out_dir> [id] [palette] [frames]` positionals interleaved with
/// `--flag value` pairs; unknown flags and missing or invalid values print
/// the usage and exit 1.
fn parse_cli() -> Cli {
    let mut args = std::env::args().skip(1);
    let mut config = Config::default();
    let mut positionals = Vec::new();
    while let Some(arg) = args.next() {
        if !arg.starts_with("--") {
            positionals.push(arg);
            continue;
        }
        let (name, value) = match arg.as_str() {
            "--paint-spread" | "--stroke-overlap" | "--settle-fraction" | "--settle-budget"
            | "--wet-darken" | "--wet-sheen-add" | "--sheen-depth" | "--reference" => {
                match args.next() {
                    Some(v) => (arg.clone(), v),
                    None => {
                        eprintln!("{arg} needs a value");
                        usage();
                    }
                }
            }
            "--max-steps" => match args.next() {
                Some(v) => (arg.clone(), v),
                None => {
                    eprintln!("--max-steps needs a value");
                    usage();
                }
            },
            _ => {
                eprintln!("unknown flag {arg:?}");
                usage();
            }
        };
        let f32_value = |name: &str, v: &str| -> f32 {
            match v.parse::<f32>() {
                Ok(x) if x.is_finite() => x,
                _ => {
                    eprintln!("{name} needs a finite number; got {v:?}");
                    usage();
                }
            }
        };
        match name.as_str() {
            "--paint-spread" => config.paint_spread = Some(f32_value("--paint-spread", &value)),
            "--stroke-overlap" => {
                config.stroke_overlap = Some(f32_value("--stroke-overlap", &value))
            }
            "--settle-fraction" => {
                config.settle_fraction = Some(f32_value("--settle-fraction", &value))
            }
            "--settle-budget" => config.settle_budget = Some(f32_value("--settle-budget", &value)),
            "--wet-darken" => config.wet_darken = Some(f32_value("--wet-darken", &value)),
            "--wet-sheen-add" => config.wet_sheen_add = Some(f32_value("--wet-sheen-add", &value)),
            "--sheen-depth" => config.sheen_depth = Some(f32_value("--sheen-depth", &value)),
            "--max-steps" => match value.parse::<u32>() {
                Ok(n) if n >= 1 => config.max_steps = Some(n),
                _ => {
                    eprintln!("--max-steps needs a positive integer; got {value:?}");
                    usage();
                }
            },
            "--reference" => config.reference = Some(PathBuf::from(&value)),
            _ => unreachable!("handled above"),
        }
    }
    if positionals.is_empty() {
        eprintln!("missing <out_dir>");
        usage();
    }
    let out_dir = PathBuf::from(positionals.remove(0));
    let id = positionals
        .first()
        .cloned()
        .unwrap_or_else(|| "header-motif".to_string());
    let palette_name = positionals
        .get(1)
        .cloned()
        .unwrap_or_else(|| "moonlight".to_string());
    let count = match positionals.get(2) {
        None => 12,
        Some(s) => match s.parse::<u32>() {
            Ok(n) if n >= 1 => n,
            _ => {
                eprintln!("frames must be a positive integer; got {s:?}");
                usage();
            }
        },
    };
    Cli {
        config,
        out_dir,
        id,
        palette_name,
        count,
    }
}

fn usage() -> ! {
    eprintln!(
        "usage: reveal_timing <out_dir> [id] [palette] [frames] [flags...]\n\
         flags: --paint-spread <f> --stroke-overlap <f> --max-steps <n>
\
                --settle-fraction <f> --settle-budget <f>
\
                --wet-darken <f> --wet-sheen-add <f> --sheen-depth <f> --reference <dir>"
    );
    std::process::exit(1);
}

fn main() {
    let cli = parse_cli();
    let out_dir = cli.out_dir.clone();
    let id = cli.id.clone();
    let palette_name = cli.palette_name.clone();
    let count = cli.count;
    let config = &cli.config;

    let Some(palette) = Palette::by_name(&palette_name) else {
        eprintln!(
            "unknown palette {palette_name:?}; known: {:?}",
            Palette::NAMES
        );
        std::process::exit(1);
    };
    let ids: Vec<&str> = if id == "all" {
        ArtworkCatalogue::ids().to_vec()
    } else if ArtworkCatalogue::ids().contains(&id.as_str()) {
        vec![id.as_str()]
    } else {
        eprintln!("unknown id {id:?}; known: {:?}", ArtworkCatalogue::ids());
        std::process::exit(1);
    };
    fs::create_dir_all(&out_dir).expect("create out dir");

    let render_params = RenderParams {
        wet_darken: config
            .wet_darken
            .unwrap_or(RenderParams::default().wet_darken),
        wet_sheen_add: config
            .wet_sheen_add
            .unwrap_or(RenderParams::default().wet_sheen_add),
        sheen_depth: config
            .sheen_depth
            .unwrap_or(RenderParams::default().sheen_depth),
        ..RenderParams::default()
    };

    let t0 = Instant::now();
    let mut gpu: Option<GpuEngine> = match GpuContext::try_new().expect("gpu context") {
        Some(ctx) => {
            let engine = GpuEngine::with_params(ctx.clone(), SimParams::default(), render_params)
                .expect("gpu engine");
            println!(
                "device init: {:.1} ms ({} via {:?})",
                t0.elapsed().as_secs_f64() * 1000.0,
                ctx.adapter_name(),
                ctx.backend()
            );
            Some(engine)
        }
        None => {
            eprintln!("no GPU adapter available; using the CPU reference");
            None
        }
    };

    let light_ground = hex(0xf7f5f0);
    let dark_ground = hex(0x12141a);

    for id in &ids {
        let dir = if ids.len() > 1 {
            out_dir.join(id)
        } else {
            out_dir.clone()
        };
        fs::create_dir_all(&dir).expect("create id dir");

        let light = config.scene_for(id, &palette, Background::Transparent);
        let (width, height) = output_size(&light, LONG_SIDE);
        println!(
            "\n== {id} ({} sim, {} ticks, palette {}, {width}x{height} out)",
            light.sim_resolution.0, light.timeline.total_ticks, light.palette.name
        );

        let (light_meas, light_route) =
            run_scene(&mut gpu, &light, count, width, height, render_params);
        for (i, f) in light_meas.frames.iter().enumerate() {
            write_png(&dir, &format!("frame-{i:02}.png"), f);
        }
        write_png(
            &dir,
            "strip.png",
            &make_sheet(&light_meas.frames, light_ground),
        );

        print_table(&light_meas.frames, &light_meas.ticks);
        let ref_dir: Option<PathBuf> = match &config.reference {
            Some(r) if ids.len() > 1 => Some(r.join(id)),
            other => other.clone(),
        };
        print_verdict(&light_meas, &light, ref_dir.as_deref());

        let dark = config.scene_for(id, &palette, Background::TransparentOnDark);
        let (dark_meas, dark_route) =
            run_scene(&mut gpu, &dark, count, width, height, render_params);
        write_png(
            &dir,
            "strip-dark.png",
            &make_sheet(&dark_meas.frames, dark_ground),
        );

        println!(
            "\nwrote frames + strip.png ({light_route}) + strip-dark.png ({dark_route}) to {}",
            dir.display()
        );
    }
}
