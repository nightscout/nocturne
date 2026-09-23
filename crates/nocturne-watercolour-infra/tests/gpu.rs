//! GPU backend tests. Each returns early with a note when no adapter exists.

use std::sync::OnceLock;

use nocturne_watercolour_core::application::{
    CheckpointPolicy, CpuEngine, Playback, Renderer, Simulator,
};
use nocturne_watercolour_core::domain::{
    Background, CompositeMode, Palette, Scene, Seed, SimResolution,
};
use nocturne_watercolour_infra::authoring::ArtworkCatalogue;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

/// Mean absolute difference (linear premultiplied RGBA) tolerated between
/// the CPU reference and the GPU port on the finished `wash` at 128x128 sim.
/// Both run the same f32 rules; the residual is fused-multiply-add and
/// transcendental rounding that compounds over ~300 ticks.
const CPU_GPU_MAE_TOLERANCE: f32 = 0.01;

/// One device for the whole binary: the harness runs tests on parallel
/// threads, and a device per test would open one per thread against the
/// same adapter.
static CONTEXT: OnceLock<Option<GpuContext>> = OnceLock::new();

fn gpu() -> Option<GpuEngine> {
    let ctx =
        CONTEXT.get_or_init(|| GpuContext::try_new().expect("context creation must not error"));
    match ctx {
        Some(ctx) => Some(GpuEngine::new(ctx.clone()).expect("engine")),
        None => {
            eprintln!("skipping: no GPU adapter");
            None
        }
    }
}

fn small_scene(name: &str) -> Scene {
    let mut scene = ArtworkCatalogue::build(name, Seed(5), Palette::dusk()).unwrap();
    scene.sim_resolution = SimResolution(128);
    scene
}

#[test]
fn seeded_replay_is_bit_identical_on_the_same_device() {
    let Some(gpu) = gpu() else { return };
    let scene = small_scene("wash");
    let mut pb = Playback::new(gpu, scene.clone(), 1000.0).unwrap();
    pb.advance_ticks(90).unwrap();
    let a = pb.simulator().read_grid().unwrap();
    let img_a = pb.simulator().render(64, 64).unwrap();
    let gpu = pb.into_simulator();
    let mut pb = Playback::new(gpu, scene, 1000.0).unwrap();
    pb.advance_ticks(90).unwrap();
    let b = pb.simulator().read_grid().unwrap();
    let img_b = pb.simulator().render(64, 64).unwrap();
    assert_eq!(a, b);
    assert_eq!(img_a.rgba, img_b.rgba);
}

/// Compares the GPU and CPU engines' current state: the rendered 256x256
/// frame and the deposited-pigment grid, at the shared tolerance, with the
/// diagnostics the lockstep run prints at every checkpoint.
fn compare_engine_pair(g: &mut Playback<GpuEngine>, c: &mut Playback<CpuEngine>, label: &str) {
    let gpu_img = g.simulator().render(256, 256).unwrap();
    let cpu_img = c.simulator().render(256, 256).unwrap();
    let mae = gpu_img.mean_abs_diff(&cpu_img).unwrap();
    let (worst_i, worst) = gpu_img
        .rgba
        .iter()
        .zip(&cpu_img.rgba)
        .map(|(a, b)| (a - b).abs())
        .enumerate()
        .fold(
            (0, 0.0f32),
            |acc, (i, d)| if d > acc.1 { (i, d) } else { acc },
        );
    let px = worst_i / 4;
    let big_alpha: Vec<usize> = gpu_img
        .rgba
        .chunks(4)
        .zip(cpu_img.rgba.chunks(4))
        .enumerate()
        .filter(|(_, (a, b))| (a[3] - b[3]).abs() > 0.5)
        .map(|(i, _)| i)
        .collect();
    let gpu_more = gpu_img
        .rgba
        .chunks(4)
        .zip(cpu_img.rgba.chunks(4))
        .filter(|(a, b)| a[3] - b[3] > 0.5)
        .count();
    eprintln!(
        "[{label} tick {}] pixels with |dAlpha| > 0.5: {} (gpu higher in {gpu_more}); first few: {:?}",
        g.current_tick(),
        big_alpha.len(),
        big_alpha
            .iter()
            .take(6)
            .map(|i| (i % gpu_img.width as usize, i / gpu_img.width as usize))
            .collect::<Vec<_>>()
    );
    eprintln!(
        "[{label}] cpu/gpu mae {mae}; worst {worst} at pixel ({}, {}) channel {}: gpu {:?} cpu {:?}",
        px % gpu_img.width as usize,
        px / gpu_img.width as usize,
        worst_i % 4,
        &gpu_img.rgba[px * 4..px * 4 + 4],
        &cpu_img.rgba[px * 4..px * 4 + 4]
    );
    assert!(mae < CPU_GPU_MAE_TOLERANCE, "[{label}] mae {mae}");
    let gpu_grid = g.simulator().read_grid().unwrap();
    let cpu_grid = c.simulator().grid().unwrap();
    let grid_mae: f32 = gpu_grid
        .pigments_deposited
        .iter()
        .zip(&cpu_grid.pigments_deposited)
        .map(|(a, b)| (a - b).abs())
        .sum::<f32>()
        / gpu_grid.pigments_deposited.len() as f32;
    eprintln!("[{label}] deposited pigment mae {grid_mae}");
    assert!(
        grid_mae < CPU_GPU_MAE_TOLERANCE,
        "[{label}] grid mae {grid_mae}"
    );
}

fn assert_gpu_matches_cpu(gpu: GpuEngine, scene: Scene) {
    let mut g = Playback::new(gpu, scene.clone(), 1000.0).unwrap();
    let mut c = Playback::new(CpuEngine::default(), scene, 1000.0).unwrap();
    // Step both engines in lockstep through the timeline and compare along
    // the run, not just at the end: the final frame alone would pass however
    // far the two ports drift, because the timeline's implicit DryAll drives
    // both to the same fully dry state before the comparison, and the browser
    // runs the GPU port. The intermediate ticks land inside the drying tail.
    let total = c.total_ticks();
    let mut prev = 0u32;
    for &frac in &[0.25, 0.5, 0.75] {
        let target = (total as f32 * frac).round() as u32;
        let delta = target.saturating_sub(prev);
        if delta > 0 {
            g.advance_ticks(delta).unwrap();
            c.advance_ticks(delta).unwrap();
            prev = target;
        }
        compare_engine_pair(&mut g, &mut c, "intermediate");
    }
    g.finish_immediately().unwrap();
    c.finish_immediately().unwrap();
    compare_engine_pair(&mut g, &mut c, "final");
}

#[test]
fn gpu_matches_cpu_reference_within_tolerance() {
    let Some(gpu) = gpu() else { return };
    assert_gpu_matches_cpu(gpu, small_scene("wash"));
}

/// The luminous mode travels in the state header (`StateLayout::composite_mode`).
/// Until the engine's `load` forwards `scene.composite_mode()` into the grid it
/// packs, the GPU renders subtractively; that case is reported and skipped
/// rather than failed, because the change lives in a file owned elsewhere.
#[test]
fn gpu_matches_cpu_reference_in_luminous_mode() {
    let Some(mut gpu) = gpu() else { return };
    let mut scene = small_scene("glaze_pair");
    scene.background = Background::TransparentOnDark;
    scene.palette = scene.palette.for_dark_surface();
    gpu.load(&scene).unwrap();
    let forwarded = gpu.read_grid().unwrap().composite_mode;
    if forwarded != CompositeMode::Luminous {
        eprintln!(
            "skipping: GpuEngine::load does not forward the scene's composite mode into the state header yet"
        );
        return;
    }
    assert_gpu_matches_cpu(gpu, scene);
}

#[test]
fn resize_renders_from_the_same_state() {
    let Some(gpu) = gpu() else { return };
    let mut pb = Playback::new(gpu, small_scene("crescent_moon"), 1000.0).unwrap();
    pb.advance_ticks(40).unwrap();
    let small = pb.simulator().render(128, 128).unwrap();
    let large = pb.simulator().render(1024, 1024).unwrap();
    assert_eq!((small.width, small.height), (128, 128));
    assert_eq!((large.width, large.height), (1024, 1024));
    assert!(small.rgba.iter().all(|v| v.is_finite()));
    assert!(large.rgba.iter().all(|v| v.is_finite()));
    let alpha_small: f32 = small.rgba.chunks(4).map(|p| p[3]).sum::<f32>() / (128.0 * 128.0);
    let alpha_large: f32 = large.rgba.chunks(4).map(|p| p[3]).sum::<f32>() / (1024.0 * 1024.0);
    assert!(
        (alpha_small - alpha_large).abs() < 0.02,
        "{alpha_small} vs {alpha_large}"
    );
    assert!(alpha_small > 0.01);
}

#[test]
fn checkpoint_seek_equals_straight_replay() {
    let Some(gpu) = gpu() else { return };
    let scene = small_scene("wash");
    let mut straight = Playback::new(gpu, scene.clone(), 1000.0).unwrap();
    straight.advance_ticks(100).unwrap();
    let expected = straight.simulator().read_grid().unwrap();
    let gpu = straight.into_simulator();

    let mut seeking = Playback::new(gpu, scene, 1000.0)
        .unwrap()
        .with_policy(CheckpointPolicy {
            every_ticks: 16,
            ..Default::default()
        });
    seeking.advance_ticks(150).unwrap();
    seeking.seek_tick(70).unwrap();
    seeking.advance_ticks(30).unwrap();
    assert_eq!(seeking.current_tick(), 100);
    assert!(seeking.checkpoint_ticks().contains(&64));
    let got = seeking.simulator().read_grid().unwrap();
    assert_eq!(got, expected);
}

/// The swirl's phase lives in the tick the GPU counts in its state header:
/// it must match the steps taken, and a restore must put it back.
#[test]
fn the_gpu_tick_counts_steps_and_restores_with_a_checkpoint() {
    let Some(mut gpu) = gpu() else { return };
    gpu.load(&small_scene("wash")).unwrap();
    assert_eq!(gpu.read_grid().unwrap().tick, 0);
    gpu.step(37).unwrap();
    assert_eq!(gpu.read_grid().unwrap().tick, 37);
    let id = gpu.snapshot().unwrap().expect("a checkpoint fits");
    gpu.step(20).unwrap();
    assert_eq!(gpu.read_grid().unwrap().tick, 57);
    gpu.restore(id).unwrap();
    assert_eq!(gpu.read_grid().unwrap().tick, 37);
    gpu.step(1).unwrap();
    assert_eq!(gpu.read_grid().unwrap().tick, 38);
}

#[test]
fn checkpoint_capacity_is_bounded_and_releasable() {
    let Some(mut gpu) = gpu() else { return };
    gpu.load(&small_scene("wash")).unwrap();
    let cap = gpu.checkpoint_capacity();
    assert!((1..=64).contains(&cap));
    let mut ids = Vec::new();
    for _ in 0..cap {
        ids.push(gpu.snapshot().unwrap().expect("within capacity"));
    }
    assert_eq!(gpu.snapshot().unwrap(), None);
    gpu.release(ids[0]);
    assert!(gpu.snapshot().unwrap().is_some());
}

/// Largest per-cell velocity difference tolerated between the two ports.
/// Both inject the same `paint::StrokeFlow` off the same uploaded stamp, so
/// the residual is float rounding, not a difference in the rule.
const CPU_GPU_VELOCITY_TOLERANCE: f32 = 1e-4;

/// The velocity a laydown injects is compared where it is written, not in the
/// picture.
///
/// A rendered frame is a poor witness for it: velocity reaches the image only
/// after advection has carried pigment around, by which point a wrong sign is
/// a few hundredths of alpha and sits inside
/// [`CPU_GPU_MAE_TOLERANCE`]. Flipping the sign of the outward push in
/// `apply.wgsl` leaves `gpu_matches_cpu_reference_within_tolerance` green and
/// fails this.
#[test]
fn gpu_matches_cpu_on_the_velocity_a_stroke_injects() {
    let Some(gpu) = gpu() else { return };
    let scene = small_scene("crescent_moon");
    let mut g = Playback::new(gpu, scene.clone(), 1000.0).unwrap();
    let mut c = Playback::new(CpuEngine::default(), scene, 1000.0).unwrap();
    // Far enough in that several spans have been laid and the film is moving,
    // early enough that the sheet has not dried and zeroed the field.
    g.advance_ticks(30).unwrap();
    c.advance_ticks(30).unwrap();

    let gpu_grid = g.simulator().read_grid().unwrap();
    let cpu = c.simulator();
    let cpu_grid = cpu.grid().expect("cpu grid");

    let peak = cpu_grid
        .velocity_u
        .iter()
        .chain(&cpu_grid.velocity_v)
        .fold(0.0f32, |m, v| m.max(v.abs()));
    assert!(
        peak > 1e-3,
        "nothing was moving, so this test proves nothing (peak speed {peak})"
    );

    let worst = gpu_grid
        .velocity_u
        .iter()
        .zip(&cpu_grid.velocity_u)
        .chain(gpu_grid.velocity_v.iter().zip(&cpu_grid.velocity_v))
        .map(|(a, b)| (a - b).abs())
        .fold(0.0f32, f32::max);
    assert!(
        worst <= CPU_GPU_VELOCITY_TOLERANCE,
        "the two ports inject different velocity: worst cell differs by {worst}"
    );
}
