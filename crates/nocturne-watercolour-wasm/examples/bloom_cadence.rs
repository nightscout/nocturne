//! Replay 60 Hz display times through a bloom and count distinct painted frames.
//!
//! `cargo run -p nocturne-watercolour-wasm --example bloom_cadence --release -- <scene.json>`

use std::collections::HashSet;
use std::fs;
use std::time::Instant;

use nocturne_watercolour_core::application::{Playback, ProgressCurve};
use nocturne_watercolour_infra::document::parse_scene_json;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

fn main() {
    let path = std::env::args().nth(1).expect("scene.json");
    let discrete = std::env::args().any(|arg| arg == "--discrete");
    let scene = parse_scene_json(&fs::read_to_string(path).expect("read scene")).expect("scene");
    let curve = ProgressCurve::reveal_for(&scene, 0.5);
    let context = GpuContext::try_new()
        .expect("GPU context")
        .expect("adapter");
    let engine = GpuEngine::new(context)
        .expect("engine")
        .with_checkpoint_budget(0);
    let mut playback = Playback::new(engine, scene, 5200.0).expect("playback");
    playback.set_progress_curve(curve);
    playback.play();
    let mut digests = HashSet::new();
    let mut stationary = 0;
    let mut previous = 0u64;
    let mut step_ms = Vec::new();
    let mut frame_ms = Vec::new();
    playback
        .simulator()
        .present_offscreen_at(160, 60, 0, 1.0)
        .expect("blank frame");
    for frame in 1u32..=156 {
        let started = Instant::now();
        playback.advance_by_elapsed(1.0 / 60.0).expect("advance");
        step_ms.push(started.elapsed().as_secs_f64() * 1000.0);
        let tick = playback.current_tick();
        let blend = playback.tick_blend();
        let image = if discrete {
            playback
                .simulator()
                .present_offscreen(160, 60, true)
                .expect("render")
                .unwrap()
        } else {
            playback
                .simulator()
                .present_offscreen_at(160, 60, tick, blend)
                .expect("render")
        };
        frame_ms.push(started.elapsed().as_secs_f64() * 1000.0);
        let digest = image.iter().fold(0xcbf29ce484222325u64, |hash, channel| {
            (hash ^ u64::from(*channel)).wrapping_mul(0x100000001b3)
        });
        if frame > 12 && digest == previous {
            stationary += 1;
        }
        previous = digest;
        digests.insert(digest);
        if frame.is_multiple_of(60) || frame == 156 {
            println!(
                "frame={frame} tick={tick} distinct_frames={}",
                digests.len()
            );
        }
    }
    println!(
        "growth: {} distinct frames / 2.6 s = {:.2} Hz; stationary refreshes after 200 ms: {stationary}",
        digests.len(),
        digests.len() as f32 / 2.6
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
    if !discrete {
        assert_eq!(
            stationary, 0,
            "growth must advance at every display refresh"
        );
    }
}
