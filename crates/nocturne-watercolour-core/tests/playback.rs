use nocturne_watercolour_core::application::cpu::checkpoint_bytes;
use nocturne_watercolour_core::application::{
    Advance, AdvanceByElapsed, CheckpointPolicy, CpuEngine, Playback, PlaybackState, ProgressCurve,
    Reveal, Simulator, settle_after_last_stroke,
};
use nocturne_watercolour_core::domain::{
    Background, BrushStroke, Operation, Palette, Paper, Point, RadiusProfile, Scene, SceneId, Seed,
    SimResolution, SimulationGrid, SizeHint, StrokeSpan, Timeline,
};

fn scene() -> Scene {
    scene_with_drop(0.2)
}

/// [`scene`] with the accent drop at `drop` instead of 0.2, so a caller can
/// control where the drawing ends (the reveal's `tick_split`).
fn scene_with_drop(drop: f32) -> Scene {
    let seed = Seed(77);
    let timeline = Reveal::new(160)
        .wash(Operation::Brush(BrushStroke {
            path: vec![Point::new(0.3, 0.5), Point::new(0.7, 0.5)],
            radius: RadiusProfile::uniform(0.18),
            pigment: 0,
            concentration: 0.5,
            water: 1.0,
            softness: 0.3,
            span: StrokeSpan::FULL,
        }))
        .drop_in(
            drop,
            Operation::Brush(BrushStroke {
                path: vec![Point::new(0.5, 0.5)],
                radius: RadiusProfile::uniform(0.06),
                pigment: 2,
                concentration: 0.8,
                water: 0.4,
                softness: 0.5,
                span: StrokeSpan::FULL,
            }),
        )
        .settle(0.6, 4.0)
        .build();
    Scene {
        id: SceneId("playback-test".into()),
        size_hint: SizeHint {
            width: 128,
            height: 128,
        },
        paper: Paper::cold_press(seed),
        palette: Palette::moonlight(),
        timeline,
        seed,
        sim_resolution: SimResolution(64),
        background: Background::Transparent,
    }
}

/// The curve, the checkpoints and the seek semantics are what this suite
/// pins, so it steps the simulation with the per-frame tick budget out of the
/// way; the budget has its own tests in `application::playback`.
fn unbudgeted(scene: Scene, duration_ms: f32) -> Playback<CpuEngine> {
    Playback::new(CpuEngine::default(), scene, duration_ms)
        .unwrap()
        .with_tick_budget(0)
}

#[test]
fn seek_then_step_equals_straight_replay() {
    let mut straight = unbudgeted(scene(), 3000.0);
    straight.advance_ticks(100).unwrap();
    let expected = straight.simulator().grid().unwrap().clone();

    let mut seeking = unbudgeted(scene(), 3000.0).with_policy(CheckpointPolicy {
        every_ticks: 16,
        ..Default::default()
    });
    seeking.advance_ticks(140).unwrap();
    seeking.seek_tick(90).unwrap();
    assert_eq!(seeking.current_tick(), 90);
    Advance { ticks: 10 }.execute(&mut seeking).unwrap();
    assert_eq!(seeking.current_tick(), 100);
    assert_eq!(seeking.simulator().grid().unwrap(), &expected);
    assert!(seeking.checkpoint_ticks().contains(&32));
}

#[test]
fn dense_events_do_not_starve_the_checkpoint_budget() {
    let seed = Seed(13);
    let mut timeline = Timeline::new(160);
    timeline.push(
        0,
        Operation::Brush(BrushStroke {
            path: vec![Point::new(0.3, 0.5), Point::new(0.7, 0.5)],
            radius: RadiusProfile::uniform(0.18),
            pigment: 0,
            concentration: 0.5,
            water: 1.0,
            softness: 0.3,
            span: StrokeSpan::FULL,
        }),
    );
    for tick in 1..=160 {
        timeline.push(tick, Operation::ClearMask);
    }
    let scene = Scene {
        id: SceneId("dense-events".into()),
        size_hint: SizeHint {
            width: 64,
            height: 64,
        },
        paper: Paper::cold_press(seed),
        palette: Palette::moonlight(),
        timeline,
        seed,
        sim_resolution: SimResolution(64),
        background: Background::Transparent,
    };
    let policy = CheckpointPolicy {
        every_ticks: 32,
        min_event_spacing: 8,
    };

    let mut seeking = unbudgeted(scene.clone(), 3000.0).with_policy(policy);
    seeking.advance_ticks(160).unwrap();
    let ticks = seeking.checkpoint_ticks();
    assert!(
        ticks
            .windows(2)
            .all(|w| w[1] - w[0] >= policy.min_event_spacing),
        "checkpoint ticks must stay spaced: {ticks:?}"
    );

    seeking.seek_tick(123).unwrap();
    assert_eq!(seeking.current_tick(), 123);
    let expected = seeking.simulator().grid().unwrap().clone();

    let mut straight = unbudgeted(scene, 3000.0);
    straight.advance_ticks(123).unwrap();
    assert_eq!(straight.simulator().grid().unwrap(), &expected);
}

#[test]
fn seek_backwards_past_all_checkpoints_reloads_and_replays() {
    let mut pb = unbudgeted(scene(), 3000.0).with_policy(CheckpointPolicy {
        every_ticks: 1000,
        ..Default::default()
    });
    pb.advance_ticks(60).unwrap();
    let expected = pb.simulator().grid().unwrap().clone();
    pb.advance_ticks(50).unwrap();
    pb.seek_tick(60).unwrap();
    assert_eq!(pb.simulator().grid().unwrap(), &expected);
}

#[test]
fn finish_immediately_ends_at_total_ticks_fully_dry() {
    let mut pb = unbudgeted(scene(), 3000.0);
    pb.advance_ticks(20).unwrap();
    pb.finish_immediately().unwrap();
    assert_eq!(pb.current_tick(), pb.total_ticks());
    assert_eq!(pb.state(), PlaybackState::Finished);
    assert_eq!(pb.progress(), 1.0);
    let grid = pb.simulator().grid().unwrap();
    assert!(grid.is_dry());
    assert!(grid.total_water() == 0.0);
    assert!(grid.pigments_in_water.iter().all(|&g| g == 0.0));
    assert!(grid.total_pigment() > 0.0);
}

#[test]
fn elapsed_time_is_front_loaded_and_accumulates_fractions() {
    // The drop at 0.3 puts the last stroke past the 20 % wall split, so the
    // reveal still front-loads ticks into the paint phase.
    let mut pb = unbudgeted(scene_with_drop(0.3), 1000.0);
    AdvanceByElapsed {
        elapsed_seconds: 0.5,
    }
    .execute(&mut pb)
    .unwrap();
    assert_eq!(pb.current_tick(), 0, "paused playback ignores elapsed time");
    pb.play();
    for _ in 0..5 {
        AdvanceByElapsed {
            elapsed_seconds: 0.1,
        }
        .execute(&mut pb)
        .unwrap();
    }
    assert_eq!(pb.current_tick(), pb.tick_for_progress(0.5));
    assert!(
        pb.current_tick() > 80,
        "half the time runs past half the ticks"
    );
    for _ in 0..5 {
        AdvanceByElapsed {
            elapsed_seconds: 0.1,
        }
        .execute(&mut pb)
        .unwrap();
    }
    assert_eq!(pb.state(), PlaybackState::Finished);
}

#[test]
fn seek_progress_then_reset_returns_to_start() {
    let mut pb = unbudgeted(scene(), 1000.0);
    pb.seek_progress(0.4).unwrap();
    assert!(pb.current_tick() > 0);
    pb.reset().unwrap();
    assert_eq!(pb.current_tick(), 0);
    assert!(pb.simulator().grid().unwrap().total_pigment() == 0.0);
}

#[test]
fn checkpoint_capacity_is_bounded() {
    let mut engine = CpuEngine::default();
    engine.load(&scene()).unwrap();
    let cap = engine.checkpoint_capacity();
    assert!((1..=64).contains(&cap));
    let mut ids = Vec::new();
    for _ in 0..cap {
        ids.push(engine.snapshot().unwrap().expect("within capacity"));
    }
    assert_eq!(engine.snapshot().unwrap(), None);
    engine.release(ids[0]);
    assert!(engine.snapshot().unwrap().is_some());
}

/// A surface that is never scrubbed asks for the smallest budget, so it holds
/// the tick-0 checkpoint and nothing else; seeking then replays from the top
/// rather than restoring a nearer state, and must land on the same grid.
#[test]
fn a_one_byte_budget_keeps_one_checkpoint_and_still_seeks_exactly() {
    let one_checkpoint = |pb: &mut Playback<CpuEngine>| {
        checkpoint_bytes(pb.simulator().grid().unwrap()) * pb.checkpoint_ticks().len()
    };

    let mut default_budget = unbudgeted(scene(), 3000.0);
    default_budget.advance_ticks(140).unwrap();

    let mut tiny = Playback::new(
        CpuEngine::default().with_checkpoint_budget(1),
        scene(),
        3000.0,
    )
    .unwrap()
    .with_tick_budget(0);
    tiny.advance_ticks(140).unwrap();

    assert_eq!(tiny.checkpoint_ticks(), vec![0]);
    assert!(
        one_checkpoint(&mut tiny) * 4 < one_checkpoint(&mut default_budget),
        "a one-byte budget held {} bytes against the default's {}",
        one_checkpoint(&mut tiny),
        one_checkpoint(&mut default_budget),
    );

    tiny.seek_tick(100).unwrap();
    assert_eq!(tiny.current_tick(), 100);
    let mut straight = unbudgeted(scene(), 3000.0);
    straight.advance_ticks(100).unwrap();
    assert_eq!(
        tiny.simulator().grid().unwrap(),
        straight.simulator().grid().unwrap()
    );
}

#[test]
fn advance_to_progress_forward_equals_straight_replay() {
    let mut stepping = unbudgeted(scene(), 3000.0);
    stepping.advance_to_progress(0.4).unwrap();
    stepping.advance_to_progress(0.7).unwrap();
    assert_eq!(stepping.current_tick(), (0.7_f32 * 160.0).round() as u32);
    let expected = stepping.simulator().grid().unwrap().clone();

    let mut straight = unbudgeted(scene(), 3000.0);
    straight
        .advance_ticks((0.7_f32 * 160.0).round() as u32)
        .unwrap();
    assert_eq!(straight.simulator().grid().unwrap(), &expected);
}

#[test]
fn advance_to_progress_backward_seeks_and_matches_fresh_replay() {
    let mut pb = unbudgeted(scene(), 3000.0).with_policy(CheckpointPolicy {
        every_ticks: 16,
        ..Default::default()
    });
    pb.advance_to_progress(0.9).unwrap();
    pb.advance_to_progress(0.5).unwrap();
    assert_eq!(pb.current_tick(), 80);
    assert_eq!(
        pb.state(),
        PlaybackState::Paused,
        "a backward advance seeks"
    );
    let expected = pb.simulator().grid().unwrap().clone();

    let mut fresh = unbudgeted(scene(), 3000.0);
    fresh.advance_ticks(80).unwrap();
    assert_eq!(fresh.simulator().grid().unwrap(), &expected);
}

#[test]
fn advance_to_progress_to_one_finishes_and_dries() {
    let mut pb = unbudgeted(scene(), 3000.0);
    pb.advance_to_progress(0.4).unwrap();
    pb.advance_to_progress(1.0).unwrap();
    assert_eq!(pb.current_tick(), pb.total_ticks());
    assert_eq!(pb.state(), PlaybackState::Finished);
    let grid = pb.simulator().grid().unwrap();
    assert!(grid.is_dry());
    assert!(grid.total_water() == 0.0);
}

#[test]
fn linear_curve_maps_progress_one_to_one() {
    let mut pb = unbudgeted(scene(), 1000.0);
    pb.set_progress_curve(ProgressCurve::Linear);
    assert_eq!(pb.tick_for_progress(0.5), 80);
    assert_eq!(pb.tick_for_progress(1.0), 160);
    pb.play();
    AdvanceByElapsed {
        elapsed_seconds: 0.5,
    }
    .execute(&mut pb)
    .unwrap();
    assert_eq!(pb.current_tick(), 80);
    assert!((pb.progress() - 0.5).abs() < 0.01);
    pb.advance_to_progress(1.0).unwrap();
    assert_eq!(pb.state(), PlaybackState::Finished);
}

/// A disc wash with an accent dropped in while wet and only the implicit
/// settle tail, so the tail is where the water leaves and the rim darkens.
fn tail_scene() -> Scene {
    let seed = Seed(99);
    // A wet two-stroke wash so the sheet still has water to give up when the
    // settle starts, and the settle is placed where the pen stops (the
    // reveal's `settle_after_last_stroke`), so the tail is the drying phase.
    let mut timeline = Reveal::new(240)
        .wash(Operation::Brush(BrushStroke {
            path: vec![Point::new(0.5, 0.5)],
            radius: RadiusProfile::uniform(0.18),
            pigment: 0,
            concentration: 0.6,
            water: 4.0,
            softness: 0.2,
            span: StrokeSpan::FULL,
        }))
        .drop_in(
            0.1,
            Operation::Brush(BrushStroke {
                path: vec![Point::new(0.5, 0.5)],
                radius: RadiusProfile::uniform(0.06),
                pigment: 2,
                concentration: 0.8,
                water: 4.0,
                softness: 0.5,
                span: StrokeSpan::FULL,
            }),
        )
        .build();
    settle_after_last_stroke(&mut timeline);
    Scene {
        id: SceneId("tail-test".into()),
        size_hint: SizeHint {
            width: 128,
            height: 128,
        },
        paper: Paper::cold_press(seed),
        palette: Palette::moonlight(),
        timeline,
        seed,
        sim_resolution: SimResolution(64),
        background: Background::Transparent,
    }
}

fn deposited_mean_abs_diff(a: &SimulationGrid, b: &SimulationGrid) -> f32 {
    a.pigments_deposited
        .iter()
        .zip(&b.pigments_deposited)
        .map(|(x, y)| (x - y).abs())
        .sum::<f32>()
        / a.pigments_deposited.len().max(1) as f32
}

fn radial_profile(grid: &SimulationGrid, pigment: usize) -> Vec<(f32, f32)> {
    let n = grid.cell_count();
    let w = grid.width as f32;
    let h = grid.height as f32;
    let c = ((w - 1.0) / 2.0, (h - 1.0) / 2.0);
    (0..n)
        .map(|i| {
            let x = (i % grid.width as usize) as f32 - c.0;
            let y = (i / grid.width as usize) as f32 - c.1;
            let r = (x * x + y * y).sqrt() / w;
            (r, grid.pigments_deposited[pigment * n + i])
        })
        .collect()
}

fn mean_in(profile: &[(f32, f32)], lo: f32, hi: f32) -> f32 {
    let vals: Vec<f32> = profile
        .iter()
        .filter(|(r, _)| *r >= lo && *r < hi)
        .map(|(_, d)| *d)
        .collect();
    vals.iter().sum::<f32>() / vals.len().max(1) as f32
}

#[test]
fn settle_tail_keeps_drying_to_the_end_and_darkens_the_rim() {
    let scene = tail_scene();
    let total = scene.timeline.total_ticks;
    let ticks: Vec<u32> = [0.55_f32, 0.75, 1.00]
        .iter()
        .map(|p| (p * total as f32).round() as u32)
        .collect();
    let grids: Vec<SimulationGrid> = ticks
        .iter()
        .map(|&t| {
            let mut pb = unbudgeted(scene.clone(), 3000.0);
            pb.advance_ticks(t).unwrap();
            pb.simulator().grid().unwrap().clone()
        })
        .collect();

    // The settle tail is the drying phase: the sheet is still giving up
    // suspended pigment into it, so the first sampled pair must differ.
    let diff = deposited_mean_abs_diff(&grids[0], &grids[1]);
    eprintln!("deposited diff across the tail: {diff}");
    assert!(
        diff > 1e-4,
        "the tail must keep changing while the sheet dries: {diff}"
    );

    let finished = &grids[2];
    assert!(finished.is_dry(), "the finished frame is fully dry");
    assert_eq!(finished.total_water(), 0.0);

    let edge_first = mean_in(&radial_profile(&grids[0], 0), 0.16, 0.24);
    let edge_final = mean_in(&radial_profile(finished, 0), 0.16, 0.24);
    eprintln!("boundary band deposited pigment: early {edge_first} final {edge_final}");
    assert!(
        edge_final > edge_first,
        "the boundary band must darken over the tail: {edge_first} -> {edge_final}"
    );
}
