//! Drives a [`Simulator`] through a scene's timeline.
//!
//! # Tick semantics
//!
//! "At tick `t`" means `t` steps have run. Advancing one tick applies every
//! event scheduled at the current tick, then steps once. Events scheduled at
//! `total_ticks` run when the end is reached, followed by an implicit
//! `DryAll`, so a scene is guaranteed fully dry when finished.
//!
//! # Artistic duration
//!
//! `duration_ms` maps wall-clock progress `p in 0..1` to ticks through one of
//! three [`ProgressCurve`]s. [`ProgressCurve::FrontLoaded`] eases the whole
//! duration with `ease(p) = 1 - (1 - p)^2`; [`ProgressCurve::Linear`] maps
//! one-for-one; the default [`ProgressCurve::Reveal`] is piecewise linear,
//! running `tick_split` of the ticks inside `wall_split` of the wall-clock
//! and the rest over the rest. Paint needs many simulation ticks inside its
//! 600 ms; drying does not, and running the 2.4 s tail at paint tick rate
//! would blow the frame budget, so the curve splits instead of easing. The
//! split is where the drawing ends — the tick of the scene's last stroke
//! event — so the wall-clock paint phase covers exactly the brushwork and the
//! tail covers the spread, bloom, settling and drying that follow it.
//! `set_progress_curve` swaps this for a linear one-to-one mapping when a
//! caller supplies its own easing via `advance_to_progress`.
//!
//! # Seeking
//!
//! Seeking never edits a timestamp. It restores the nearest checkpoint at or
//! before the target and replays deterministically from there, so seeking
//! then stepping is bit-identical to a straight run.
//!
//! # Checkpoint policy and memory bound
//!
//! A checkpoint is taken at tick 0, at every tick that carries a timeline
//! event at least `CheckpointPolicy::min_event_spacing` ticks after the
//! newest checkpoint, and every `CheckpointPolicy::every_ticks` ticks,
//! subject to the backend's `checkpoint_capacity`. Event checkpoints are
//! never evicted, so the spacing floor stops a dense event stream from
//! filling the budget with them and starving the periodic rule. When the
//! budget is full, the oldest periodic (non-event) checkpoint after tick 0
//! is released to make room; if only event checkpoints remain, no more are
//! taken and seeks replay further. Memory is therefore
//! `capacity * checkpoint_bytes`, where the backend derives capacity from
//! its own budget.

use crate::domain::{Operation, Scene, SubSeed};

use super::ports::{CheckpointId, DabCharge, EngineError, Simulator};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlaybackState {
    Paused,
    Playing,
    Finished,
}

/// How `advance_by_elapsed` and `seek_progress` map progress to ticks.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ProgressCurve {
    /// `ease(p) = 1 - (1 - p)^2`: the first half of the duration runs three
    /// quarters of the ticks, so the wash lands fast and the tail settles.
    FrontLoaded,
    /// `tick = round(p * total_ticks)`, one-for-one; for callers driving with
    /// `advance_to_progress` and their own easing, so `progress()` stays in
    /// the same space they drive in.
    Linear,
    /// Wall-clock `wall_split` of the duration runs `tick_split` of the
    /// ticks, the rest of the duration runs the rest: the reveal paints in
    /// its first fifth and spends the remainder setting into the page.
    /// `tick_split` is where the drawing ends (the scene's last stroke
    /// event), not where an evaporation rate changes. Constructed only
    /// through [`ProgressCurve::reveal_for`], which clamps both splits away
    /// from the ends.
    Reveal { wall_split: f32, tick_split: f32 },
}

/// Fraction of wall-clock the paint phase gets for a [`ProgressCurve::Reveal`].
pub const DEFAULT_PAINT_WALL_FRACTION: f32 = 0.2;

/// Both reveal splits are clamped away from the ends so neither phase can
/// degenerate to zero length.
const SPLIT_MIN: f32 = 0.05;
const SPLIT_MAX: f32 = 0.95;

impl ProgressCurve {
    /// The reveal curve for `scene`: the paint phase ends where the pen
    /// leaves the paper — the scene's last stroke event — so the wall-clock
    /// tail covers exactly the ticks the sheet spends spreading, settling and
    /// drying. A timeline with no strokes falls back to its last `Dry`, then
    /// to `0.3`.
    pub fn reveal_for(scene: &Scene, wall_split: f32) -> ProgressCurve {
        let total = scene.timeline.total_ticks.max(1) as f32;
        let last_stroke = scene
            .timeline
            .events
            .iter()
            .rev()
            .find_map(|e| match &e.op {
                Operation::Brush(_)
                | Operation::Dab(_)
                | Operation::Water(_)
                | Operation::Lift(_) => Some(e.at_tick as f32 / total),
                _ => None,
            });
        let tick_split = match last_stroke {
            Some(t) => t,
            None => scene
                .timeline
                .events
                .iter()
                .rev()
                .find_map(|e| match &e.op {
                    Operation::Dry { .. } => Some(e.at_tick as f32 / total),
                    _ => None,
                })
                .unwrap_or(0.3),
        };
        ProgressCurve::Reveal {
            wall_split: wall_split.clamp(SPLIT_MIN, SPLIT_MAX),
            tick_split: tick_split.clamp(SPLIT_MIN, SPLIT_MAX),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CheckpointPolicy {
    pub every_ticks: u32,
    /// Minimum gap in ticks between event checkpoints, so a dense event
    /// stream cannot fill the budget with never-evicted checkpoints.
    pub min_event_spacing: u32,
}

impl Default for CheckpointPolicy {
    fn default() -> Self {
        CheckpointPolicy {
            every_ticks: 32,
            min_event_spacing: 8,
        }
    }
}

const EASE_EXPONENT: f32 = 2.0;

pub fn ease(progress: f32) -> f32 {
    let p = progress.clamp(0.0, 1.0);
    1.0 - (1.0 - p).powf(EASE_EXPONENT)
}

pub fn ease_inverse(eased: f32) -> f32 {
    let e = eased.clamp(0.0, 1.0);
    1.0 - (1.0 - e).powf(1.0 / EASE_EXPONENT)
}

/// Progress -> tick fraction for [`ProgressCurve::Reveal`], piecewise linear
/// through `(0,0)`, `(wall_split, tick_split)`, `(1,1)`.
pub fn reveal_ticks(progress: f32, wall_split: f32, tick_split: f32) -> f32 {
    let p = progress.clamp(0.0, 1.0);
    let w = wall_split.clamp(SPLIT_MIN, SPLIT_MAX);
    let t = tick_split.clamp(SPLIT_MIN, SPLIT_MAX);
    if p <= w {
        p / w * t
    } else {
        t + (p - w) / (1.0 - w) * (1.0 - t)
    }
}

/// Exact inverse of [`reveal_ticks`].
pub fn reveal_progress(tick_fraction: f32, wall_split: f32, tick_split: f32) -> f32 {
    let t = tick_fraction.clamp(0.0, 1.0);
    let w = wall_split.clamp(SPLIT_MIN, SPLIT_MAX);
    let ts = tick_split.clamp(SPLIT_MIN, SPLIT_MAX);
    if t <= ts {
        t / ts * w
    } else {
        w + (t - ts) / (1.0 - ts) * (1.0 - w)
    }
}

#[derive(Debug, Clone, Copy)]
struct Checkpoint {
    tick: u32,
    id: CheckpointId,
    at_event: bool,
}

/// Simulation ticks a single wall-clock advance may run.
///
/// A reveal is driven one animation frame at a time, and a tick is the
/// expensive thing in a frame. Without a ceiling the cost of a frame is
/// whatever the frame before it took: a hitch asks for more ticks, which makes
/// the next frame longer, which asks for more still. The ceiling breaks that
/// loop by letting the reveal *slip* instead — it finishes a little late
/// rather than stuttering, which for a decorative hero is the trade nobody
/// notices.
///
/// It also makes the brushwork's speed a property of the artwork rather than
/// of the machine: every device that can afford the budget draws at the same
/// rate, and one that cannot falls behind smoothly instead of dropping frames.
///
/// Six is fitted to the cost of a tick on the grids live mode uses. Measured
/// on a discrete laptop GPU a tick at 256 cells costs 0.35-1.06 ms, so six is
/// a few milliseconds of a 16 ms frame there and still leaves headroom on an
/// integrated GPU several times slower. For scale, Sudo Aquarelle — the
/// reference this simulation follows — runs two substeps a frame on a grid
/// thirteen times larger.
pub const DEFAULT_TICK_BUDGET: u32 = 6;

pub struct Playback<S: Simulator> {
    sim: S,
    scene: Scene,
    tick: u32,
    state: PlaybackState,
    duration_ms: f32,
    elapsed_progress: f32,
    curve: ProgressCurve,
    policy: CheckpointPolicy,
    tick_budget: u32,
    checkpoints: Vec<Checkpoint>,
}

impl<S: Simulator> Playback<S> {
    pub fn new(mut sim: S, scene: Scene, duration_ms: f32) -> Result<Self, EngineError> {
        scene
            .validate()
            .map_err(|e| EngineError::new(format!("invalid scene: {e:?}")))?;
        if !duration_ms.is_finite() || duration_ms <= 0.0 {
            return Err(EngineError::new("duration_ms must be positive"));
        }
        sim.load(&scene)?;
        let curve = ProgressCurve::reveal_for(&scene, DEFAULT_PAINT_WALL_FRACTION);
        let mut pb = Playback {
            sim,
            scene,
            tick: 0,
            state: PlaybackState::Paused,
            duration_ms,
            elapsed_progress: 0.0,
            curve,
            policy: CheckpointPolicy::default(),
            tick_budget: DEFAULT_TICK_BUDGET,
            checkpoints: Vec::new(),
        };
        pb.take_checkpoint(true)?;
        Ok(pb)
    }

    pub fn with_policy(mut self, policy: CheckpointPolicy) -> Self {
        self.policy = CheckpointPolicy {
            every_ticks: policy.every_ticks.max(1),
            min_event_spacing: policy.min_event_spacing,
        };
        self
    }

    /// Caps the ticks one [`Self::advance_by_elapsed`] or
    /// [`Self::advance_to_progress`] may run; see [`DEFAULT_TICK_BUDGET`].
    /// Zero is read as no cap, which is what the bake and the native examples
    /// want — they are not drawing frames to a clock.
    pub fn with_tick_budget(mut self, ticks: u32) -> Self {
        self.tick_budget = ticks;
        self
    }

    pub fn tick_budget(&self) -> u32 {
        self.tick_budget
    }

    /// The ticks [`Self::advance_by_elapsed`] would run now for `elapsed_seconds`.
    pub fn ticks_due(&self, elapsed_seconds: f32) -> u32 {
        if self.state != PlaybackState::Playing || !elapsed_seconds.is_finite() {
            return 0;
        }
        let target = self.tick_for_progress(self.progress_after(elapsed_seconds));
        self.budgeted(target).saturating_sub(self.tick)
    }

    /// The ticks [`Self::advance_to_progress`] would run forward now for `progress`.
    pub fn ticks_due_at_progress(&self, progress: f32) -> u32 {
        self.budgeted(self.linear_tick(progress))
            .saturating_sub(self.tick)
    }

    /// The furthest tick one advance may reach from where it is now.
    fn budgeted(&self, target: u32) -> u32 {
        if self.tick_budget == 0 {
            target
        } else {
            target.min(self.tick.saturating_add(self.tick_budget))
        }
    }

    pub fn scene(&self) -> &Scene {
        &self.scene
    }

    pub fn simulator(&mut self) -> &mut S {
        &mut self.sim
    }

    pub fn into_simulator(self) -> S {
        self.sim
    }

    pub fn current_tick(&self) -> u32 {
        self.tick
    }

    pub fn total_ticks(&self) -> u32 {
        self.scene.timeline.total_ticks
    }

    pub fn state(&self) -> PlaybackState {
        self.state
    }

    pub fn duration_ms(&self) -> f32 {
        self.duration_ms
    }

    /// Artistic progress `0..1` corresponding to the current tick.
    pub fn progress(&self) -> f32 {
        let t = self.tick as f32 / self.total_ticks() as f32;
        match self.curve {
            ProgressCurve::FrontLoaded => ease_inverse(t),
            ProgressCurve::Linear => t,
            ProgressCurve::Reveal {
                wall_split,
                tick_split,
            } => reveal_progress(t, wall_split, tick_split),
        }
    }

    pub fn tick_for_progress(&self, progress: f32) -> u32 {
        self.fractional_tick(progress).round() as u32
    }

    /// Presentation lags simulation by half a tick, blending only states already simulated.
    pub fn tick_blend(&self) -> f32 {
        if self.state != PlaybackState::Playing {
            return 1.0;
        }
        (self.fractional_tick(self.elapsed_progress) - self.tick as f32 + 0.5).clamp(0.0, 1.0)
    }

    fn fractional_tick(&self, progress: f32) -> f32 {
        let p = progress.clamp(0.0, 1.0);
        match self.curve {
            ProgressCurve::FrontLoaded => ease(p) * self.total_ticks() as f32,
            ProgressCurve::Linear => p * self.total_ticks() as f32,
            ProgressCurve::Reveal {
                wall_split,
                tick_split,
            } => reveal_ticks(p, wall_split, tick_split) * self.total_ticks() as f32,
        }
    }

    /// Switches the curve `advance_by_elapsed` and `seek_progress` map progress
    /// with; callers that drive `advance_to_progress` with their own easing set
    /// `Linear` so `progress()` reads the same space they drive in.
    pub fn set_progress_curve(&mut self, curve: ProgressCurve) {
        self.curve = curve;
        self.elapsed_progress = self.progress();
    }

    pub fn play(&mut self) {
        if self.state != PlaybackState::Finished {
            self.state = PlaybackState::Playing;
        }
    }

    pub fn pause(&mut self) {
        if self.state == PlaybackState::Playing {
            self.state = PlaybackState::Paused;
        }
    }

    pub fn reset(&mut self) -> Result<(), EngineError> {
        self.seek_tick(0)?;
        self.state = PlaybackState::Paused;
        self.elapsed_progress = 0.0;
        Ok(())
    }

    /// Advances `ticks` steps regardless of play state, finishing at the end.
    pub fn advance_ticks(&mut self, ticks: u32) -> Result<(), EngineError> {
        let target = self.tick.saturating_add(ticks).min(self.total_ticks());
        self.run_to(target)?;
        self.elapsed_progress = self.elapsed_progress.max(self.progress());
        Ok(())
    }

    /// Consumes wall-clock time while playing; fractional ticks accumulate in
    /// the stored progress until they amount to whole ticks.
    pub fn advance_by_elapsed(&mut self, elapsed_seconds: f32) -> Result<(), EngineError> {
        if self.state != PlaybackState::Playing || !elapsed_seconds.is_finite() {
            return Ok(());
        }
        self.elapsed_progress = self.progress_after(elapsed_seconds);
        let target = self.tick_for_progress(self.elapsed_progress);
        if target > self.tick {
            let reached = self.budgeted(target);
            self.run_to(reached)?;
        }
        Ok(())
    }

    /// Advances to the tick `round(progress * total_ticks)` with no internal
    /// easing: steps forward when the target is ahead, seeks (checkpoint
    /// restore + replay) only when it is behind. Callers apply their own
    /// easing before calling, so this can be driven from any curve.
    pub fn advance_to_progress(&mut self, progress: f32) -> Result<(), EngineError> {
        let p = progress.clamp(0.0, 1.0);
        let target = self.linear_tick(p);
        if target > self.tick {
            let reached = self.budgeted(target);
            self.run_to(reached)?;
            if reached < target {
                self.elapsed_progress = self.progress();
                return Ok(());
            }
        } else if target < self.tick {
            // A seek is a checkpoint restore plus a replay, not a frame's
            // worth of simulation, so the budget does not apply to it.
            self.seek_tick(target)?;
        }
        self.elapsed_progress = p;
        Ok(())
    }

    fn progress_after(&self, elapsed_seconds: f32) -> f32 {
        (self.elapsed_progress + elapsed_seconds.max(0.0) * 1000.0 / self.duration_ms).min(1.0)
    }

    fn linear_tick(&self, progress: f32) -> u32 {
        (progress.clamp(0.0, 1.0) * self.total_ticks() as f32).round() as u32
    }

    pub fn seek_progress(&mut self, progress: f32) -> Result<(), EngineError> {
        let target = self.tick_for_progress(progress);
        self.seek_tick(target)?;
        self.elapsed_progress = progress.clamp(0.0, 1.0);
        Ok(())
    }

    pub fn seek_tick(&mut self, target: u32) -> Result<(), EngineError> {
        let target = target.min(self.total_ticks());
        if target < self.tick || self.state == PlaybackState::Finished {
            self.restore_for_seek(target)?;
        }
        self.run_to(target)?;
        self.elapsed_progress = self.progress();
        Ok(())
    }

    /// Restores at most one checkpoint, then replays at most `ticks` steps
    /// towards `target`, and returns the steps replayed; the seek has arrived
    /// once `current_tick` is `target`. Repeated calls may replace the target
    /// without completing an obsolete replay.
    pub fn seek_towards_tick(&mut self, target: u32, ticks: u32) -> Result<u32, EngineError> {
        let target = target.min(self.total_ticks());
        if target < self.tick {
            self.restore_for_seek(target)?;
        }
        self.pause();
        let from = self.tick;
        self.run_to(from.saturating_add(ticks).min(target))?;
        self.elapsed_progress = self.progress();
        Ok(self.tick - from)
    }

    fn restore_for_seek(&mut self, target: u32) -> Result<(), EngineError> {
        let cp = self
            .checkpoints
            .iter()
            .filter(|c| c.tick <= target)
            .max_by_key(|c| c.tick)
            .copied();
        match cp {
            Some(cp) => {
                self.sim.restore(cp.id)?;
                self.tick = cp.tick;
            }
            None => {
                self.sim.load(&self.scene)?;
                self.checkpoints.clear();
                self.tick = 0;
                self.take_checkpoint(true)?;
            }
        }
        self.state = PlaybackState::Paused;
        Ok(())
    }

    /// Runs the remaining ticks, applies the end-of-timeline events and dries
    /// everything.
    pub fn finish_immediately(&mut self) -> Result<(), EngineError> {
        self.run_to(self.total_ticks())?;
        self.elapsed_progress = 1.0;
        Ok(())
    }

    /// Ticks from the current one that can run as one `Simulator::step`: up
    /// to `target`, the next event, or the next tick a periodic checkpoint
    /// could be taken at, whichever comes first. A backend encodes a step as
    /// one batch, where one tick at a time is a submission each. With
    /// `batch_dabs`, dabs do not end the run: `step_with_dabs` charges them
    /// inside it, and only other operations are boundaries.
    fn run_length(&self, target: u32, batch_dabs: bool) -> u32 {
        let next_event = self
            .scene
            .timeline
            .events
            .iter()
            .filter(|e| !batch_dabs || !matches!(e.op, Operation::Dab(_)))
            .map(|e| e.at_tick)
            .filter(|&t| t > self.tick)
            .min()
            .unwrap_or(u32::MAX);
        let mut stop = target.min(next_event);
        if self.sim.checkpoint_capacity() > 0 {
            let every = self.policy.every_ticks;
            stop = stop.min((self.tick / every + 1).saturating_mul(every));
        }
        stop - self.tick
    }

    fn run_to(&mut self, target: u32) -> Result<(), EngineError> {
        while self.tick < target {
            let batch_dabs = self.sim.checkpoint_capacity() == 0 && {
                let mut events = self.scene.timeline.events_at(self.tick).peekable();
                events.peek().is_some() && events.all(|(_, e)| matches!(e.op, Operation::Dab(_)))
            };
            let ticks = self.run_length(target, batch_dabs);
            if batch_dabs {
                let charges: Vec<_> = self
                    .scene
                    .timeline
                    .events
                    .iter()
                    .enumerate()
                    .filter(|(_, e)| e.at_tick >= self.tick && e.at_tick < self.tick + ticks)
                    .filter_map(|(index, e)| match &e.op {
                        Operation::Dab(dab) => Some(DabCharge {
                            at_tick: e.at_tick - self.tick,
                            dab,
                            seed: self.scene.seed.derive(SubSeed::Brush(index as u32)),
                        }),
                        _ => None,
                    })
                    .collect();
                self.sim.step_with_dabs(ticks, &charges)?;
            } else {
                self.apply_events_at(self.tick)?;
                self.sim.step(ticks)?;
            }
            self.tick += ticks;
            if self.tick < self.total_ticks() {
                let at_event = self.scene.timeline.events_at(self.tick).next().is_some();
                let periodic = self.tick.is_multiple_of(self.policy.every_ticks);
                if at_event {
                    let newest = self
                        .checkpoints
                        .iter()
                        .map(|c| c.tick)
                        .filter(|&t| t <= self.tick)
                        .max()
                        .unwrap_or(0);
                    if self.tick.saturating_sub(newest) >= self.policy.min_event_spacing {
                        self.take_checkpoint(true)?;
                    } else if periodic {
                        self.take_checkpoint(false)?;
                    }
                } else if periodic {
                    self.take_checkpoint(false)?;
                }
            }
        }
        if self.tick == self.total_ticks() && self.state != PlaybackState::Finished {
            self.apply_events_at(self.tick)?;
            self.sim.apply(&Operation::DryAll, self.scene.seed)?;
            self.state = PlaybackState::Finished;
        }
        Ok(())
    }

    fn apply_events_at(&mut self, tick: u32) -> Result<(), EngineError> {
        let events: Vec<(usize, Operation)> = self
            .scene
            .timeline
            .events_at(tick)
            .map(|(i, e)| (i, e.op.clone()))
            .collect();
        for (index, op) in events {
            let seed = self.scene.seed.derive(SubSeed::Brush(index as u32));
            self.sim.apply(&op, seed)?;
        }
        Ok(())
    }

    fn take_checkpoint(&mut self, at_event: bool) -> Result<(), EngineError> {
        if self.checkpoints.iter().any(|c| c.tick == self.tick) {
            return Ok(());
        }
        let capacity = self.sim.checkpoint_capacity();
        if capacity == 0 {
            return Ok(());
        }
        if self.checkpoints.len() >= capacity {
            let victim = self
                .checkpoints
                .iter()
                .position(|c| !c.at_event && c.tick != 0);
            match victim {
                Some(pos) => {
                    let removed = self.checkpoints.remove(pos);
                    self.sim.release(removed.id);
                }
                None => return Ok(()),
            }
        }
        if let Some(id) = self.sim.snapshot()? {
            self.checkpoints.push(Checkpoint {
                tick: self.tick,
                id,
                at_event,
            });
        }
        Ok(())
    }

    pub fn checkpoint_ticks(&self) -> Vec<u32> {
        let mut t: Vec<u32> = self.checkpoints.iter().map(|c| c.tick).collect();
        t.sort_unstable();
        t
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::application::CpuEngine;
    use crate::domain::{
        Background, BrushStroke, Dab, Palette, Paper, Point, RadiusProfile, SceneId, Seed,
        SimResolution, SizeHint, StrokeSpan, Timeline,
    };

    fn reveal_scene(timeline: Timeline) -> Scene {
        let seed = Seed(7);
        Scene {
            id: SceneId("reveal-test".into()),
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
        }
    }

    fn budget_scene() -> Scene {
        let mut t = Timeline::new(400);
        t.push(
            0,
            Operation::Brush(BrushStroke {
                path: vec![Point::new(0.2, 0.5), Point::new(0.8, 0.5)],
                radius: RadiusProfile::uniform(0.1),
                pigment: 0,
                concentration: 0.5,
                water: 0.8,
                softness: 0.4,
                span: StrokeSpan::FULL,
            }),
        );
        t.push(200, Operation::Dry { rate: 3.0 });
        reveal_scene(t)
    }

    /// The frame that arrives late must not make the next frame worse. One
    /// advance runs at most the budget however much wall clock it is handed.
    #[test]
    fn one_advance_never_runs_more_than_its_tick_budget() {
        let mut pb = Playback::new(CpuEngine::default(), budget_scene(), 1000.0)
            .unwrap()
            .with_tick_budget(3);
        pb.play();
        // Ten seconds against a one-second reveal: the whole timeline, asked
        // for at once.
        pb.advance_by_elapsed(10.0).unwrap();
        assert_eq!(pb.current_tick(), 3, "one advance ran past its budget");
        let before = pb.current_tick();
        pb.advance_by_elapsed(10.0).unwrap();
        assert_eq!(pb.current_tick() - before, 3, "the shortfall compounded");
    }

    #[test]
    fn a_budgeted_reveal_slips_but_still_arrives() {
        let mut pb = Playback::new(CpuEngine::default(), budget_scene(), 1000.0)
            .unwrap()
            .with_tick_budget(4);
        pb.play();
        let mut frames = 0;
        while pb.current_tick() < pb.total_ticks() && frames < 1000 {
            let before = pb.current_tick();
            pb.advance_by_elapsed(1.0 / 60.0).unwrap();
            assert!(
                pb.current_tick() - before <= 4,
                "a frame ran {} ticks on a budget of 4",
                pb.current_tick() - before
            );
            frames += 1;
        }
        assert_eq!(
            pb.current_tick(),
            pb.total_ticks(),
            "the reveal never arrived"
        );
        assert!(
            frames > 60,
            "a budget of 4 cannot finish 400 ticks in {frames} frames"
        );
    }

    #[test]
    fn skipped_clock_time_is_retained_while_each_advance_stays_bounded() {
        let mut playback = Playback::new(CpuEngine::default(), budget_scene(), 1000.0)
            .unwrap()
            .with_tick_budget(4);
        playback.play();
        playback.advance_by_elapsed(1.0).unwrap();
        assert_eq!(playback.current_tick(), 4);
        while playback.state() != PlaybackState::Finished {
            let before = playback.current_tick();
            playback.advance_by_elapsed(0.0).unwrap();
            assert!(playback.current_tick() - before <= 4);
        }
        let mut reference = Playback::new(CpuEngine::default(), budget_scene(), 1000.0).unwrap();
        reference.finish_immediately().unwrap();
        assert_eq!(playback.simulator().grid(), reference.simulator().grid());
    }

    #[test]
    fn ticks_due_are_the_ticks_the_next_advance_runs() {
        let mut clock = Playback::new(CpuEngine::default(), budget_scene(), 1000.0)
            .unwrap()
            .with_tick_budget(4);
        assert_eq!(clock.ticks_due(0.1), 0, "paused");
        clock.play();
        for elapsed in [0.0, 0.004, 0.016, 0.1, 0.0, 0.5] {
            let due = clock.ticks_due(elapsed);
            let before = clock.current_tick();
            clock.advance_by_elapsed(elapsed).unwrap();
            assert_eq!(clock.current_tick() - before, due, "after {elapsed} s");
        }
        let mut eased = Playback::new(CpuEngine::default(), budget_scene(), 1000.0)
            .unwrap()
            .with_tick_budget(4);
        for progress in [0.001, 0.004, 0.02, 0.02, 0.5] {
            let due = eased.ticks_due_at_progress(progress);
            let before = eased.current_tick();
            eased.advance_to_progress(progress).unwrap();
            assert_eq!(eased.current_tick() - before, due, "at {progress}");
        }
        assert_eq!(eased.ticks_due_at_progress(0.0), 0, "backwards is a seek");
    }

    /// Records the step sizes a playback asks for.
    struct Steps {
        inner: CpuEngine,
        steps: Vec<u32>,
        capacity: usize,
    }

    impl Simulator for Steps {
        fn load(&mut self, scene: &Scene) -> Result<(), EngineError> {
            self.inner.load(scene)
        }
        fn apply(&mut self, op: &Operation, seed: crate::domain::Seed) -> Result<(), EngineError> {
            self.inner.apply(op, seed)
        }
        fn tick(&mut self) -> Result<(), EngineError> {
            self.step(1)
        }
        fn step(&mut self, ticks: u32) -> Result<(), EngineError> {
            if ticks > 0 {
                self.steps.push(ticks);
            }
            self.inner.step(ticks)
        }
        fn snapshot(&mut self) -> Result<Option<CheckpointId>, EngineError> {
            self.inner.snapshot()
        }
        fn restore(&mut self, id: CheckpointId) -> Result<(), EngineError> {
            self.inner.restore(id)
        }
        fn release(&mut self, id: CheckpointId) {
            self.inner.release(id)
        }
        fn checkpoint_capacity(&self) -> usize {
            self.capacity.min(self.inner.checkpoint_capacity())
        }
    }

    /// Ticks between events run as one step, broken only where a periodic
    /// checkpoint may be taken, and not there when none can be.
    #[test]
    fn a_run_between_events_is_one_step() {
        for (capacity, expected) in [
            (
                usize::MAX,
                vec![32, 32, 32, 32, 32, 32, 8, 24, 32, 32, 32, 32, 32, 16],
            ),
            (0, vec![200, 200]),
        ] {
            let sim = Steps {
                inner: CpuEngine::default(),
                steps: Vec::new(),
                capacity,
            };
            let mut pb = Playback::new(sim, budget_scene(), 1000.0).unwrap();
            pb.finish_immediately().unwrap();
            assert_eq!(pb.simulator().steps, expected, "capacity {capacity}");
            assert_eq!(pb.state(), PlaybackState::Finished);
        }
    }

    /// Without checkpoints, ticks with no dab run up to the first dab, and the
    /// runs between later dabs each reach `step` whole.
    #[test]
    fn a_dab_batch_steps_whole_runs_between_charges() {
        let dab = |x| {
            Operation::Dab(Dab {
                center: Point::new(x, 0.5),
                radius: 0.1,
                pigment: 0,
                concentration: 0.5,
                water: 0.8,
                softness: 0.4,
            })
        };
        let mut timeline = Timeline::new(100);
        timeline.push(10, dab(0.3));
        timeline.push(30, dab(0.6));
        let sim = Steps {
            inner: CpuEngine::default(),
            steps: Vec::new(),
            capacity: 0,
        };
        let mut pb = Playback::new(sim, reveal_scene(timeline), 1000.0).unwrap();
        pb.finish_immediately().unwrap();
        assert_eq!(pb.simulator().steps, vec![10, 20, 70]);
    }

    /// A batched run lands on the same state as one tick at a time.
    #[test]
    fn a_batched_run_matches_ticking_one_at_a_time() {
        let mut batched = Playback::new(CpuEngine::default(), budget_scene(), 1000.0).unwrap();
        batched.finish_immediately().unwrap();
        let mut single = Playback::new(CpuEngine::default(), budget_scene(), 1000.0).unwrap();
        while single.current_tick() < single.total_ticks() {
            single.advance_ticks(1).unwrap();
        }
        assert_eq!(batched.simulator().grid(), single.simulator().grid());
    }

    #[test]
    fn a_zero_budget_is_uncapped() {
        let mut pb = Playback::new(CpuEngine::default(), budget_scene(), 1000.0)
            .unwrap()
            .with_tick_budget(0);
        pb.play();
        pb.advance_by_elapsed(10.0).unwrap();
        assert_eq!(pb.current_tick(), pb.total_ticks());
    }

    #[test]
    fn easing_is_monotone_front_loaded_and_invertible() {
        assert_eq!(ease(0.0), 0.0);
        assert_eq!(ease(1.0), 1.0);
        assert!(ease(0.5) > 0.5);
        for i in 0..=10 {
            let p = i as f32 / 10.0;
            assert!((ease_inverse(ease(p)) - p).abs() < 1e-5);
        }
    }

    #[test]
    fn reveal_curve_is_monotone_and_invertible() {
        let (wall, tick) = (0.2_f32, 0.65_f32);
        assert_eq!(reveal_ticks(0.0, wall, tick), 0.0);
        assert_eq!(reveal_ticks(1.0, wall, tick), 1.0);
        let mut prev = 0.0_f32;
        for i in 0..=20 {
            let p = i as f32 / 20.0;
            let t = reveal_ticks(p, wall, tick);
            assert!(t >= prev, "tick fraction must be monotone at {p}");
            prev = t;
            assert!(
                (reveal_progress(t, wall, tick) - p).abs() < 1e-5,
                "round-trip failed at {p}"
            );
        }
        let mut prev = 0.0_f32;
        for i in 0..=20 {
            let t = i as f32 / 20.0;
            let p = reveal_progress(t, wall, tick);
            assert!(p >= prev, "progress must be monotone at {t}");
            prev = p;
            assert!(
                (reveal_ticks(p, wall, tick) - t).abs() < 1e-5,
                "round-trip failed at {t}"
            );
        }
    }

    #[test]
    fn reveal_curve_splits_wall_clock_from_ticks() {
        let (wall, tick) = (0.2_f32, 0.65_f32);
        assert!((reveal_ticks(0.2, wall, tick) - 0.65).abs() < 1e-5);
        assert!((reveal_ticks(0.1, wall, tick) - 0.325).abs() < 1e-5);
        assert!((reveal_ticks(0.6, wall, tick) - 0.825).abs() < 1e-5);
    }

    #[test]
    fn reveal_for_reads_the_last_laydown_off_the_scene() {
        let brush = Operation::Brush(BrushStroke {
            path: vec![Point::new(0.2, 0.5), Point::new(0.8, 0.5)],
            radius: RadiusProfile::uniform(0.1),
            pigment: 0,
            concentration: 0.5,
            water: 0.8,
            softness: 0.3,
            span: StrokeSpan::FULL,
        });

        // The normal case: the last stroke is well before the settle, so the
        // paint phase ends where the drawing ends, not at the evaporation
        // change.
        let mut normal = Timeline::new(100);
        normal.push(40, brush.clone());
        normal.push(90, Operation::Dry { rate: 1.0 });
        let ProgressCurve::Reveal { tick_split, .. } =
            ProgressCurve::reveal_for(&reveal_scene(normal), 0.2)
        else {
            panic!("expected reveal");
        };
        assert!((tick_split - 0.4).abs() < 1e-5);

        // No strokes: fall back to the last Dry.
        let mut with_dry = Timeline::new(100);
        with_dry.push(70, Operation::Dry { rate: 1.0 });
        let ProgressCurve::Reveal { tick_split, .. } =
            ProgressCurve::reveal_for(&reveal_scene(with_dry), 0.2)
        else {
            panic!("expected reveal");
        };
        assert!((tick_split - 0.7).abs() < 1e-5);

        // No strokes and no Dry: a fixed 0.3.
        let empty = Timeline::new(100);
        let ProgressCurve::Reveal { tick_split, .. } =
            ProgressCurve::reveal_for(&reveal_scene(empty), 0.2)
        else {
            panic!("expected reveal");
        };
        assert!((tick_split - 0.3).abs() < 1e-5);

        // The split is clamped away from both ends.
        let mut at_start = Timeline::new(100);
        at_start.push(0, brush.clone());
        let ProgressCurve::Reveal { tick_split, .. } =
            ProgressCurve::reveal_for(&reveal_scene(at_start), 0.2)
        else {
            panic!("expected reveal");
        };
        assert_eq!(tick_split, SPLIT_MIN);

        let mut at_end = Timeline::new(100);
        at_end.push(100, brush);
        let ProgressCurve::Reveal { tick_split, .. } =
            ProgressCurve::reveal_for(&reveal_scene(at_end), 0.2)
        else {
            panic!("expected reveal");
        };
        assert_eq!(tick_split, SPLIT_MAX);
    }

    #[test]
    fn splits_are_clamped_away_from_the_ends() {
        let mut dry_at_zero = Timeline::new(100);
        dry_at_zero.push(0, Operation::Dry { rate: 1.0 });
        let ProgressCurve::Reveal { tick_split, .. } =
            ProgressCurve::reveal_for(&reveal_scene(dry_at_zero), 0.0)
        else {
            panic!("expected reveal");
        };
        assert_eq!(tick_split, SPLIT_MIN);
        assert_eq!(
            reveal_ticks(0.5, 0.0, 0.5),
            reveal_ticks(0.5, SPLIT_MIN, 0.5)
        );

        let mut dry_at_end = Timeline::new(100);
        dry_at_end.push(100, Operation::Dry { rate: 1.0 });
        let ProgressCurve::Reveal {
            wall_split,
            tick_split,
        } = ProgressCurve::reveal_for(&reveal_scene(dry_at_end), 2.0)
        else {
            panic!("expected reveal");
        };
        assert_eq!(wall_split, SPLIT_MAX);
        assert_eq!(tick_split, SPLIT_MAX);
    }

    #[test]
    fn default_curve_is_the_reveal_curve() {
        let mut timeline = Timeline::new(100);
        timeline.push(0, Operation::ClearMask);
        timeline.push(70, Operation::Dry { rate: 1.0 });
        // The curve, not the frame budget, is what this pins.
        let mut pb = Playback::new(CpuEngine::default(), reveal_scene(timeline), 3000.0)
            .unwrap()
            .with_tick_budget(0);
        pb.play();
        pb.advance_by_elapsed(0.6).unwrap();
        assert_eq!(pb.current_tick(), 70);
        pb.advance_by_elapsed(2.4).unwrap();
        assert_eq!(pb.current_tick(), pb.total_ticks());
        assert_eq!(pb.state(), PlaybackState::Finished);
    }
}
