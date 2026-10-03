//! Replays display refreshes through a bloom's paint phase and counts the
//! distinct frames it presents, blended between ticks or, with `--discrete`,
//! tick by tick. See `docs/watercolour/verification.md`.
//!
//! `cargo run -p nocturne-watercolour-wasm --example bloom_cadence --release -- <scene.json> [--discrete]`

use std::collections::HashSet;
use std::fs;
use std::time::Instant;

use nocturne_watercolour_core::application::{Playback, ProgressCurve};
use nocturne_watercolour_infra::document::parse_scene_json;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

/// `GlucoseTileBloom`'s reveal length and its `tail`.
const DURATION_MS: f32 = 5200.0;
const TAIL: f32 = 0.5;
const PAINT_WALL_FRACTION: f32 = 1.0 - TAIL;
const DISPLAY_HZ: f32 = 60.0;
const PAINT_FRAMES: u32 = (DURATION_MS * PAINT_WALL_FRACTION * DISPLAY_HZ / 1000.0) as u32;
/// Refreshes before the first charge has spread enough to change every frame.
const LANDING_FRAMES: u32 = (0.2 * DISPLAY_HZ) as u32;
/// The tile's 8:3 aspect at a size cheap to read back.
const FRAME_WIDTH: u32 = 160;
const FRAME_HEIGHT: u32 = 60;

fn main() {
    let path = std::env::args().nth(1).expect("scene.json");
    let discrete = std::env::args().any(|arg| arg == "--discrete");
    let scene = parse_scene_json(&fs::read_to_string(path).expect("read scene")).expect("scene");
    let curve = ProgressCurve::reveal_for(&scene, PAINT_WALL_FRACTION);
    let context = GpuContext::try_new()
        .expect("GPU context")
        .expect("adapter");
    let engine = GpuEngine::new(context)
        .expect("engine")
        .with_checkpoint_budget(0);
    let mut playback = Playback::new(engine, scene, DURATION_MS).expect("playback");
    playback.set_progress_curve(curve);
    playback.play();
    let mut digests = HashSet::new();
    let mut stationary = 0;
    let mut previous = 0u64;
    let mut step_ms = Vec::new();
    let mut frame_ms = Vec::new();
    playback
        .simulator()
        .present_offscreen_at(FRAME_WIDTH, FRAME_HEIGHT, 0, 1.0)
        .expect("blank frame");
    for frame in 1..=PAINT_FRAMES {
        let started = Instant::now();
        playback
            .advance_by_elapsed(1.0 / DISPLAY_HZ)
            .expect("advance");
        step_ms.push(started.elapsed().as_secs_f64() * 1000.0);
        let tick = playback.current_tick();
        let blend = playback.tick_blend();
        let image = if discrete {
            playback
                .simulator()
                .present_offscreen(FRAME_WIDTH, FRAME_HEIGHT, true)
                .expect("render")
                .unwrap()
        } else {
            playback
                .simulator()
                .present_offscreen_at(FRAME_WIDTH, FRAME_HEIGHT, tick, blend)
                .expect("render")
        };
        frame_ms.push(started.elapsed().as_secs_f64() * 1000.0);
        let digest = image.iter().fold(0xcbf29ce484222325u64, |hash, channel| {
            (hash ^ u64::from(*channel)).wrapping_mul(0x100000001b3)
        });
        if frame > LANDING_FRAMES && digest == previous {
            stationary += 1;
        }
        previous = digest;
        digests.insert(digest);
        if frame.is_multiple_of(DISPLAY_HZ as u32) || frame == PAINT_FRAMES {
            println!(
                "frame={frame} tick={tick} distinct_frames={}",
                digests.len()
            );
        }
    }
    let paint_seconds = PAINT_FRAMES as f32 / DISPLAY_HZ;
    println!(
        "growth: {} distinct frames / {paint_seconds:.1} s = {:.2} Hz; stationary refreshes after landing: {stationary}",
        digests.len(),
        digests.len() as f32 / paint_seconds
    );
    for (name, mut samples) in [
        ("CPU advance", step_ms),
        ("frame including native readback", frame_ms),
    ] {
        samples.sort_by(f64::total_cmp);
        println!(
            "{name}: median {:.3} ms, p95 {:.3} ms",
            samples[samples.len() / 2],
            samples[samples.len() * 95 / 100]
        );
    }
    println!("commands: {:?}", playback.simulator().command_counts());
}
