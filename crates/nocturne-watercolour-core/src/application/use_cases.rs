//! Commands a host issues. Each validates its input against the domain rules
//! before touching a port.

use crate::domain::{
    Operation, Palette, Paper, Scene, Seed, SimResolution, Timeline, ValidationError,
};

use super::playback::Playback;
use super::ports::{EngineError, Exporter, Renderer, Simulator};

pub struct CreateScene;

impl CreateScene {
    pub fn execute(scene: Scene) -> Result<Scene, Vec<ValidationError>> {
        scene.validate()?;
        Ok(scene)
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum SceneUpdate {
    Palette(Palette),
    Paper(Paper),
    Timeline(Timeline),
    Seed(Seed),
    SimResolution(SimResolution),
}

pub struct UpdateScene;

impl UpdateScene {
    /// Applies `update` only if the result validates; otherwise the scene is
    /// left untouched.
    pub fn execute(scene: &mut Scene, update: SceneUpdate) -> Result<(), Vec<ValidationError>> {
        let mut candidate = scene.clone();
        match update {
            SceneUpdate::Palette(p) => candidate.palette = p,
            SceneUpdate::Paper(p) => candidate.paper = p,
            SceneUpdate::Timeline(t) => candidate.timeline = t,
            SceneUpdate::Seed(s) => {
                candidate.seed = s;
                candidate.paper.seed = s.derive(crate::domain::SubSeed::Paper);
            }
            SceneUpdate::SimResolution(r) => candidate.sim_resolution = r,
        }
        candidate.validate()?;
        *scene = candidate;
        Ok(())
    }
}

/// Interactive painting outside the timeline.
pub struct ApplyOperation;

impl ApplyOperation {
    pub fn execute(
        sim: &mut dyn Simulator,
        scene: &Scene,
        op: &Operation,
        seed: Seed,
    ) -> Result<(), EngineError> {
        let pigment = match op {
            Operation::Brush(b) => Some(b.pigment),
            Operation::Dab(d) => Some(d.pigment),
            _ => None,
        };
        if let Some(pigment) = pigment
            && pigment >= scene.palette.len()
        {
            return Err(EngineError::new(format!(
                "pigment index {} out of range for palette of {}",
                pigment,
                scene.palette.len()
            )));
        }
        sim.apply(op, seed)
    }
}

pub struct Advance {
    pub ticks: u32,
}

impl Advance {
    pub fn execute<S: Simulator>(&self, playback: &mut Playback<S>) -> Result<(), EngineError> {
        playback.advance_ticks(self.ticks)
    }
}

/// Caller-supplied wall-clock delta; this layer never schedules anything.
pub struct AdvanceByElapsed {
    pub elapsed_seconds: f32,
}

impl AdvanceByElapsed {
    pub fn execute<S: Simulator>(&self, playback: &mut Playback<S>) -> Result<(), EngineError> {
        playback.advance_by_elapsed(self.elapsed_seconds)
    }
}

pub struct ExportFinished {
    pub width: u32,
    pub height: u32,
}

impl ExportFinished {
    pub fn execute<E: Simulator + Renderer>(
        &self,
        playback: &mut Playback<E>,
        exporter: &dyn Exporter,
    ) -> Result<Vec<u8>, EngineError> {
        playback.finish_immediately()?;
        let image = playback.simulator().render(self.width, self.height)?;
        exporter.encode(&image)
    }
}
