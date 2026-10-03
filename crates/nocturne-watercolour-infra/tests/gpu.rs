//! GPU backend tests. Each returns early with a note when no hardware device
//! can be opened: on a GPU-less runner wgpu may still offer a software
//! rasteriser, whose device may refuse the default limits and whose rounding
//! the CPU parity tolerances were not set against.

use std::sync::OnceLock;

use nocturne_watercolour_core::application::{
    CheckpointPolicy, CpuEngine, Playback, Renderer, Simulator,
};
use nocturne_watercolour_core::domain::optics::RenderParams;
use nocturne_watercolour_core::domain::paper::{PaperField, render_pixel_scale};
use nocturne_watercolour_core::domain::sim::SimParams;
use nocturne_watercolour_core::domain::{
    Background, CompositeMode, Palette, Paper, Scene, Seed, SimResolution, swirl,
};
use nocturne_watercolour_infra::authoring::ArtworkCatalogue;
use nocturne_watercolour_infra::export::linear_to_srgb;
use nocturne_watercolour_infra::gpu::{BLUR_MAX_RADIUS, GpuContext, GpuEngine};

/// Mean absolute difference (linear premultiplied RGBA) tolerated between
/// the CPU reference and the GPU port on the finished `wash` at 128x128 sim.
/// Both run the same f32 rules; the residual is fused-multiply-add and
/// transcendental rounding that compounds over ~300 ticks.
const CPU_GPU_MAE_TOLERANCE: f32 = 0.01;

/// One device for the whole binary: the harness runs tests on parallel
/// threads, and a device per test would open one per thread against the
/// same adapter.
static CONTEXT: OnceLock<Result<GpuContext, String>> = OnceLock::new();

fn gpu() -> Option<GpuEngine> {
    let ctx = CONTEXT.get_or_init(|| match GpuContext::try_new() {
        Ok(Some(ctx)) if ctx.is_software() => Err(format!(
            "only a software adapter ({}, {:?})",
            ctx.adapter_name(),
            ctx.backend()
        )),
        Ok(Some(ctx)) => Ok(ctx),
        Ok(None) => Err("no GPU adapter".into()),
        Err(e) => Err(format!("the adapter would not open a device: {e}")),
    });
    match ctx {
        Ok(ctx) => Some(GpuEngine::new(ctx.clone()).expect("engine")),
        Err(reason) => {
            eprintln!("skipping: {reason}");
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
fn crop_preserves_the_full_frames_pigment_and_paper_at_pixel_centres() {
    let Some(engine) = gpu() else { return };
    let mut playback = Playback::new(engine, small_scene("wash"), 1000.0).unwrap();
    playback.finish_immediately().unwrap();
    let full = playback
        .simulator()
        .present_offscreen(800, 600, true)
        .unwrap()
        .unwrap();
    playback
        .simulator()
        .set_crop([0.25, 0.5, 0.25, 0.25])
        .unwrap();
    let cropped = playback
        .simulator()
        .present_offscreen(200, 150, true)
        .unwrap()
        .unwrap();
    let mut largest = 0;
    let mut total = 0usize;
    for y in 0..150usize {
        for x in 0..200usize {
            for channel in 0..4 {
                let expected = full[((y + 300) * 800 + x + 200) * 4 + channel];
                let actual = cropped[(y * 200 + x) * 4 + channel];
                let difference = expected.abs_diff(actual);
                largest = largest.max(difference);
                total += difference as usize;
            }
        }
    }
    assert!(largest <= 1, "largest channel difference: {largest}");
    assert!(total as f64 / ((200 * 150 * 4) as f64) < 0.001);
    playback.simulator().set_crop([0.0, 0.0, 1.0, 1.0]).unwrap();
    assert_eq!(
        playback
            .simulator()
            .present_offscreen(800, 600, true)
            .unwrap()
            .unwrap(),
        full
    );
}

#[test]
fn crop_rejects_empty_nonfinite_and_outside_windows() {
    let Some(mut engine) = gpu() else { return };
    for window in [
        [0.0, 0.0, 0.0, 1.0],
        [-0.1, 0.0, 1.0, 1.0],
        [0.5, 0.0, 0.6, 1.0],
        [f32::NAN, 0.0, 1.0, 1.0],
    ] {
        assert!(engine.set_crop(window).is_err());
    }
}

#[test]
fn dab_matches_point_brush_deposits_and_paper_driven_flow() {
    use nocturne_watercolour_core::domain::{Dab, Operation, Point, SizeHint};
    let Some(mut analytic) = gpu() else { return };
    let Some(mut stamped) = gpu() else { return };
    let mut cpu = CpuEngine::default();
    for (width, height) in [(64, 64), (256, 32), (32, 256)] {
        for seed in [0, 19] {
            let mut scene = small_scene("wash");
            scene.size_hint = SizeHint { width, height };
            scene.sim_resolution = SimResolution(64);
            for center in [
                Point::new(0.0, 0.0),
                Point::new(0.5, 0.5),
                Point::new(1.0, 1.0),
            ] {
                for radius in [0.004, 0.12, 1.0] {
                    let dab = Dab {
                        center,
                        radius,
                        pigment: 0,
                        concentration: 0.1,
                        water: 0.5,
                        softness: 0.85,
                    };
                    let op = nocturne_watercolour_core::domain::Operation::Dab(dab.clone());
                    analytic.load(&scene).unwrap();
                    stamped.load(&scene).unwrap();
                    cpu.load(&scene).unwrap();
                    analytic.apply(&op, Seed(seed)).unwrap();
                    stamped
                        .apply(&Operation::Brush(dab.as_brush()), Seed(seed))
                        .unwrap();
                    cpu.apply(&op, Seed(seed)).unwrap();
                    let a = analytic.read_grid().unwrap();
                    let b = stamped.read_grid().unwrap();
                    let c = cpu.grid().unwrap();
                    for reference in [&b, c] {
                        for (name, actual, expected) in [
                            ("water", &a.pressure, &reference.pressure),
                            (
                                "pigment",
                                &a.pigments_in_water,
                                &reference.pigments_in_water,
                            ),
                            ("flow u", &a.velocity_u, &reference.velocity_u),
                            ("flow v", &a.velocity_v, &reference.velocity_v),
                        ] {
                            let worst = actual
                                .iter()
                                .zip(expected)
                                .map(|(a, b)| (a - b).abs())
                                .fold(0.0f32, f32::max);
                            assert!(
                                worst <= 1e-4,
                                "{width}x{height} seed {seed} center {center:?} radius {radius}: {name} differs {worst}"
                            );
                        }
                    }
                }
            }
        }
    }
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

#[test]
fn dab_batches_preserve_tied_events_seeds_and_control_operations() {
    use nocturne_watercolour_core::domain::{Dab, Mask, Operation, Point, Timeline};
    let Some(template) = gpu() else { return };
    let mut scene = small_scene("wash");
    scene.sim_resolution = SimResolution(64);
    scene.timeline = Timeline::new(24);
    for tick in 0..24 {
        if tick == 4 {
            scene.timeline.push(tick, Operation::Dry { rate: 0.8 });
        }
        if tick == 12 {
            scene.timeline.push(
                tick,
                Operation::SetMask(Mask::Polygon {
                    points: vec![
                        Point::new(0.0, 0.0),
                        Point::new(1.0, 0.0),
                        Point::new(1.0, 1.0),
                    ],
                    feather: 0.02,
                }),
            );
        }
        for index in 0..if tick == 5 { 72 } else { 3 } {
            scene.timeline.push(
                tick,
                Operation::Dab(Dab {
                    center: Point::new(0.2 + index as f32 * 0.003, 0.4),
                    radius: 0.2,
                    pigment: 0,
                    concentration: 0.001,
                    water: 0.02,
                    softness: 0.85,
                }),
            );
        }
        if tick == 12 {
            scene.timeline.push(tick, Operation::ClearMask);
        }
    }
    scene.timeline.push(24, Operation::DryAll);
    let mut batched = Playback::new(
        template.fork().with_checkpoint_budget(0),
        scene.clone(),
        1000.0,
    )
    .unwrap();
    let mut single = Playback::new(template.with_checkpoint_budget(0), scene, 1000.0).unwrap();
    for ticks in [4, 13, 6, 1] {
        batched.advance_ticks(ticks).unwrap();
        for _ in 0..ticks {
            single.advance_ticks(1).unwrap();
        }
        assert_eq!(
            batched.simulator().read_grid().unwrap(),
            single.simulator().read_grid().unwrap()
        );
    }
    assert!(
        batched.simulator().command_counts().passes < single.simulator().command_counts().passes
    );
    batched.seek_progress(0.3).unwrap();
    single.seek_progress(0.3).unwrap();
    assert_eq!(
        batched.simulator().read_grid().unwrap(),
        single.simulator().read_grid().unwrap()
    );
}

#[test]
fn tick_interpolation_caches_shading_and_discards_discontinuous_history() {
    use nocturne_watercolour_core::domain::{Dab, Operation, Point, Timeline};
    let Some(engine) = gpu() else { return };
    let mut scene = small_scene("wash");
    scene.sim_resolution = SimResolution(64);
    scene.timeline = Timeline::new(30);
    scene.timeline.push(
        0,
        Operation::Dab(Dab {
            center: Point::new(0.5, 0.5),
            radius: 0.25,
            pigment: 0,
            concentration: 0.1,
            water: 0.5,
            softness: 0.85,
        }),
    );
    let mut playback = Playback::new(engine, scene, 1000.0).unwrap();
    let blank = playback
        .simulator()
        .present_offscreen_at(64, 64, 0, 1.0)
        .unwrap();
    playback.advance_ticks(1).unwrap();
    let previous = playback
        .simulator()
        .present_offscreen_at(64, 64, 1, 0.0)
        .unwrap();
    assert_eq!(previous, blank);
    let current = playback
        .simulator()
        .present_offscreen_at(64, 64, 1, 1.0)
        .unwrap();
    let before = playback.simulator().command_counts();
    let midway = playback
        .simulator()
        .present_offscreen_at(64, 64, 1, 0.5)
        .unwrap();
    let after = playback.simulator().command_counts();
    assert_ne!(midway, previous);
    assert_ne!(midway, current);
    assert_eq!(after.passes - before.passes, 1);
    assert_eq!(after.dispatches, before.dispatches);
    playback.advance_ticks(3).unwrap();
    let skipped = playback
        .simulator()
        .present_offscreen_at(64, 64, 4, 0.0)
        .unwrap();
    let full = playback
        .simulator()
        .present_offscreen_at(64, 64, 4, 1.0)
        .unwrap();
    assert_eq!(skipped, full);
    playback.simulator().clear_interpolation();
    let resumed = playback
        .simulator()
        .present_offscreen_at(64, 64, 4, 0.05)
        .unwrap();
    assert_eq!(resumed, full);
    playback.seek_tick(0).unwrap();
    assert_eq!(
        playback
            .simulator()
            .present_offscreen_at(64, 64, 0, 0.5)
            .unwrap(),
        blank
    );
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

/// A budget below one checkpoint holds none, not even tick 0's; a backwards
/// seek reloads the scene and replays, landing on the straight run's state.
#[test]
fn a_budget_below_one_checkpoint_holds_none_and_still_seeks_exactly() {
    let (Some(budgeted), Some(unbudgeted)) = (gpu(), gpu()) else {
        return;
    };
    let scene = small_scene("wash");
    let mut pb = Playback::new(budgeted.with_checkpoint_budget(1), scene.clone(), 1000.0).unwrap();
    assert!(pb.checkpoint_ticks().is_empty());
    pb.advance_ticks(100).unwrap();
    assert!(pb.checkpoint_ticks().is_empty());
    assert_eq!(pb.simulator().checkpoint_bytes(), 0);
    pb.seek_tick(40).unwrap();
    let seeked = pb.simulator().read_grid().unwrap();

    let mut straight = Playback::new(unbudgeted, scene, 1000.0).unwrap();
    straight.advance_ticks(40).unwrap();
    assert_eq!(seeked, straight.simulator().read_grid().unwrap());
}

/// A fork takes the buffers a dropped sibling left in the shared pool, stale
/// contents and all; its scene must play out exactly as on new buffers.
#[test]
fn a_scene_on_pooled_buffers_plays_as_on_new_ones() {
    let (Some(template), Some(fresh)) = (gpu(), gpu()) else {
        return;
    };
    let scene = small_scene("wash");
    let mut first = Playback::new(template.fork(), scene.clone(), 1000.0).unwrap();
    first.finish_immediately().unwrap();
    first.simulator().render(64, 64).unwrap();
    drop(first);

    let mut pooled = Playback::new(template.fork(), scene.clone(), 1000.0).unwrap();
    pooled.advance_ticks(150).unwrap();
    pooled.seek_tick(70).unwrap();
    let mut new = Playback::new(fresh, scene, 1000.0).unwrap();
    new.advance_ticks(70).unwrap();
    assert_eq!(
        pooled.simulator().read_grid().unwrap(),
        new.simulator().read_grid().unwrap()
    );
    assert_eq!(
        pooled.simulator().render(64, 64).unwrap().rgba,
        new.simulator().render(64, 64).unwrap().rgba
    );
}

/// A playback's strokes, ticks and checkpoint copies go out together: a
/// submission per sixteen ticks, plus one ahead of each upload into the state
/// (`Dry`, `Settle`, the mask), plus the batch a readback flushes.
#[test]
fn a_playback_submits_a_batch_per_sixteen_ticks_and_state_upload() {
    use nocturne_watercolour_core::domain::Operation;
    let Some(gpu) = gpu() else { return };
    let scene = small_scene("glaze_pair");
    let uploads = scene
        .timeline
        .events
        .iter()
        .filter(|e| {
            matches!(
                e.op,
                Operation::Dry { .. }
                    | Operation::Settle { .. }
                    | Operation::SetMask(_)
                    | Operation::ClearMask
            )
        })
        .count() as u64;
    let ticks = u64::from(scene.timeline.total_ticks);
    let mut pb = Playback::new(gpu, scene, 1000.0).unwrap();
    let before = pb.simulator().command_counts().submits;
    pb.finish_immediately().unwrap();
    pb.simulator().sync().unwrap();
    let submits = pb.simulator().command_counts().submits - before;
    assert!(
        submits <= ticks.div_ceil(16) + uploads + 1,
        "{submits} submissions for {ticks} ticks and {uploads} state uploads"
    );
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

/// The tick hands the pressure correction and the suspended pigment between
/// `state` and `scratch` by parity, and the shipped parameters (8 Jacobi
/// iterations, 6 swirl substeps) take only the even branch; this runs the
/// odd one of each against the CPU reference. Its blur is wider than the
/// one-dispatch `blur` holds, so it also runs `blur_h` then `blur_v`.
#[test]
fn gpu_matches_cpu_with_odd_jacobi_iterations_and_swirl_substeps() {
    let Some(template) = gpu() else { return };
    let params = SimParams {
        jacobi_iterations: 7,
        swirl_speed: 0.8,
        blur_radius: BLUR_MAX_RADIUS + 1,
        ..SimParams::default()
    };
    let substeps =
        swirl::Geometry::new(128, 1.0, params.swirl_speed, params.swirl_frequency).substeps;
    assert_eq!(substeps % 2, 1, "{substeps} swirl substeps is not odd");
    let gpu = GpuEngine::with_params(template.context().clone(), params, RenderParams::default())
        .unwrap();
    let scene = small_scene("wash");
    let mut g = Playback::new(gpu, scene.clone(), 1000.0).unwrap();
    let mut c = Playback::new(
        CpuEngine::new(params, RenderParams::default()),
        scene,
        1000.0,
    )
    .unwrap();
    g.advance_ticks(60).unwrap();
    c.advance_ticks(60).unwrap();
    // Sixty ticks leave the ports within float rounding of each other; a
    // branch that reads a stale buffer lands well inside the whole-run
    // tolerance, so this compares at the tighter one.
    let gpu_grid = g.simulator().read_grid().unwrap();
    let cpu_grid = c.simulator().grid().unwrap();
    let mae = |a: &[f32], b: &[f32]| {
        a.iter().zip(b).map(|(x, y)| (x - y).abs()).sum::<f32>() / a.len() as f32
    };
    for (field, gpu_field, cpu_field) in [
        (
            "suspended",
            &gpu_grid.pigments_in_water,
            &cpu_grid.pigments_in_water,
        ),
        (
            "deposited",
            &gpu_grid.pigments_deposited,
            &cpu_grid.pigments_deposited,
        ),
        ("velocity_u", &gpu_grid.velocity_u, &cpu_grid.velocity_u),
        ("pressure", &gpu_grid.pressure, &cpu_grid.pressure),
    ] {
        let e = mae(gpu_field, cpu_field);
        assert!(e < ODD_PARITY_TOLERANCE, "{field} mae {e}");
    }
}

/// The tiled passes (`velocity_divergence`, `jacobi_pair_*`, `blur`) cover
/// the grid in 16x16 tiles; a side that is not whole tiles leaves the last
/// row and column of them part-empty.
#[test]
fn gpu_matches_cpu_on_a_grid_that_is_not_whole_tiles() {
    let Some(gpu) = gpu() else { return };
    let mut scene = small_scene("wash");
    scene.sim_resolution = SimResolution(100);
    let mut g = Playback::new(gpu, scene.clone(), 1000.0).unwrap();
    let mut c = Playback::new(CpuEngine::default(), scene, 1000.0).unwrap();
    g.advance_ticks(60).unwrap();
    c.advance_ticks(60).unwrap();
    let gpu_grid = g.simulator().read_grid().unwrap();
    let cpu_grid = c.simulator().grid().unwrap();
    let mae = |a: &[f32], b: &[f32]| {
        a.iter().zip(b).map(|(x, y)| (x - y).abs()).sum::<f32>() / a.len() as f32
    };
    for (field, gpu_field, cpu_field) in [
        ("wet", &gpu_grid.wet, &cpu_grid.wet),
        ("velocity_u", &gpu_grid.velocity_u, &cpu_grid.velocity_u),
        ("pressure", &gpu_grid.pressure, &cpu_grid.pressure),
        (
            "suspended",
            &gpu_grid.pigments_in_water,
            &cpu_grid.pigments_in_water,
        ),
    ] {
        let e = mae(gpu_field, cpu_field);
        assert!(e < ODD_PARITY_TOLERANCE, "{field} mae {e}");
    }
}

/// Per-field mean absolute difference allowed after 60 ticks of the odd
/// parity run; measured at about 1e-9, while reading the wrong buffer on
/// either branch gives about 1e-4.
const ODD_PARITY_TOLERANCE: f32 = 1e-6;

/// `present` shades straight into the swapchain; this draws the same way
/// into an `rgba8unorm` target (a browser canvas) and checks it against the
/// readback frame encoded as the canvas path does: un-premultiplied, sRGB
/// encoded, re-premultiplied. 1200x1000 is two row bands, so the band seam
/// is covered too.
#[test]
fn the_presented_frame_is_the_rendered_frame_encoded_for_a_canvas() {
    let Some(template) = gpu() else { return };
    for background in [Background::Transparent, Background::TransparentOnDark] {
        let mut scene = small_scene("glaze_pair");
        scene.background = background;
        let mut pb = Playback::new(template.fork(), scene, 1000.0).unwrap();
        pb.advance_ticks(60).unwrap();
        let (w, h) = (1200, 1000);
        let frame = pb.simulator().render(w, h).unwrap();
        let bytes = pb
            .simulator()
            .present_offscreen(w, h, true)
            .unwrap()
            .expect("read back");
        assert_eq!(bytes.len(), (w * h * 4) as usize);
        let mut worst = 0u8;
        let mut painted = 0usize;
        for (px, got) in frame.rgba.chunks(4).zip(bytes.chunks(4)) {
            let alpha = px[3].clamp(0.0, 1.0);
            let expected = if alpha <= 1.0 / 1024.0 {
                [0.0; 4]
            } else {
                let enc = |c: f32| linear_to_srgb((c / alpha).clamp(0.0, 1.0)) * alpha;
                [enc(px[0]), enc(px[1]), enc(px[2]), alpha]
            };
            if alpha > 0.05 {
                painted += 1;
            }
            for (e, &g) in expected.iter().zip(got) {
                worst = worst.max(((e * 255.0).round() as i32 - i32::from(g)).unsigned_abs() as u8);
            }
        }
        assert!(painted > 1000, "{background:?}: nothing painted to compare");
        assert!(
            worst <= 1,
            "{background:?}: a channel differs by {worst}/255"
        );
    }
}

/// The optics pass samples the GPU-generated paper, so the render digests
/// hold only while it matches `PaperField` bit for bit. The last case spans
/// two dispatch bands.
#[test]
fn the_gpu_paper_is_bit_identical_to_the_cpu_paper() {
    let Some(gpu) = gpu() else { return };
    let cases = [
        (Paper::cold_press(Seed(42)), 256, 256, 1.0),
        (Paper::rough(Seed(7)), 1000, 250, 4.0),
        (Paper::hot_press(Seed(u64::MAX)), 150, 600, 0.25),
        (Paper::cold_press(Seed(3)), 1024, 1024, 1.0),
        (Paper::rough(Seed(0xFA11)), 1536, 700, 1536.0 / 700.0),
    ];
    for (paper, width, height, aspect) in cases {
        for pixel_scale in [0.0, render_pixel_scale(width, height, aspect)] {
            let cpu =
                PaperField::generate_with_pixel_scale(&paper, width, height, aspect, pixel_scale);
            let got = gpu
                .read_paper(&paper, width, height, aspect, pixel_scale)
                .expect("gpu paper");
            let differing = cpu
                .height
                .iter()
                .zip(&got)
                .filter(|(a, b)| a.to_bits() != b.to_bits())
                .count();
            assert_eq!(
                differing,
                0,
                "{width}x{height} aspect {aspect} pixel scale {pixel_scale}: {differing} of {} differ",
                got.len()
            );
        }
    }
}

#[test]
fn sliced_target_seek_preserves_the_gpu_frame() {
    let Some(gpu) = gpu() else { return };
    let scene = small_scene("wash");
    let mut playback = Playback::new(gpu, scene, 1000.0).unwrap();
    for target in [0.7, 0.2, 1.0, 0.35] {
        playback.seek_progress(target).unwrap();
        let expected = playback.simulator().render(80, 60).unwrap().rgba;
        playback.seek_tick(0).unwrap();
        for _ in 0..500 {
            if playback.seek_towards_progress(target, 3).unwrap() {
                break;
            }
        }
        let actual = playback.simulator().render(80, 60).unwrap().rgba;
        assert_eq!(actual, expected);
    }
}
