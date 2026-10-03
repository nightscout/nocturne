//! Narrow interfaces a backend implements.

use crate::domain::{Dab, Image, Operation, Scene, Seed};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineError(pub String);

impl EngineError {
    pub fn new(msg: impl Into<String>) -> Self {
        EngineError(msg.into())
    }
}

impl std::fmt::Display for EngineError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for EngineError {}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct CheckpointId(pub u64);

pub struct DabCharge<'a> {
    pub at_tick: u32,
    pub dab: &'a Dab,
    pub seed: Seed,
}

/// Holds the simulation state for one scene and advances it.
pub trait Simulator {
    /// Resets to the empty grid for `scene`, discarding all checkpoints.
    fn load(&mut self, scene: &Scene) -> Result<(), EngineError>;

    /// `seed` drives brush jitter; callers derive it from the scene seed and
    /// event index so replay is exact.
    fn apply(&mut self, op: &Operation, seed: Seed) -> Result<(), EngineError>;

    fn tick(&mut self) -> Result<(), EngineError>;

    fn step(&mut self, ticks: u32) -> Result<(), EngineError> {
        for _ in 0..ticks {
            self.tick()?;
        }
        Ok(())
    }

    /// Charge ticks are relative to this batch; tied charges retain timeline order.
    fn step_with_dabs(&mut self, ticks: u32, charges: &[DabCharge<'_>]) -> Result<(), EngineError> {
        let mut charges = charges.iter().peekable();
        for tick in 0..ticks {
            while charges.peek().is_some_and(|charge| charge.at_tick == tick) {
                let charge = charges.next().unwrap();
                self.apply(&Operation::Dab(charge.dab.clone()), charge.seed)?;
            }
            self.tick()?;
        }
        Ok(())
    }

    /// Captures the current state. `None` means the backend's checkpoint
    /// budget is spent and the caller must release one first.
    fn snapshot(&mut self) -> Result<Option<CheckpointId>, EngineError>;

    fn restore(&mut self, id: CheckpointId) -> Result<(), EngineError>;

    fn release(&mut self, id: CheckpointId);

    /// How many checkpoints this backend will hold for the loaded scene.
    fn checkpoint_capacity(&self) -> usize;
}

/// Produces a premultiplied linear RGBA image of the current state at any
/// output size; the bounded sim grid is resampled to fit.
pub trait Renderer {
    fn render(&mut self, width: u32, height: u32) -> Result<Image, EngineError>;
}

pub trait Exporter {
    fn encode(&self, image: &Image) -> Result<Vec<u8>, EngineError>;
}
