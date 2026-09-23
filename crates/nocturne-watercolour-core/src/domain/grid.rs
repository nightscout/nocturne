//! Simulation state as plain data. Field semantics follow Curtis et al.;
//! `sim` holds the update rules.

use super::optics::CompositeMode;
use super::paper::PaperField;
use super::seed::{Seed, SubSeed};

/// All per-cell fields are row-major `width * height` vectors; per-pigment
/// fields are `pigment_count` such vectors back to back, so pigment `k` of
/// cell `i` sits at `k * cell_count + i`.
#[derive(Debug, Clone, PartialEq)]
pub struct SimulationGrid {
    pub width: u32,
    pub height: u32,
    pub pigment_count: usize,
    /// Wet-area mask `M`, `1` where water is present.
    pub wet: Vec<f32>,
    pub velocity_u: Vec<f32>,
    pub velocity_v: Vec<f32>,
    /// Water depth, which doubles as the pressure the velocity gradient reads.
    pub pressure: Vec<f32>,
    /// Pigment suspended in water, `g`.
    pub pigments_in_water: Vec<f32>,
    /// Pigment settled on the paper, `d`.
    pub pigments_deposited: Vec<f32>,
    /// Capillary saturation `s`.
    pub saturation: Vec<f32>,
    /// Capillary capacity `c`.
    pub capacity: Vec<f32>,
    /// Paper height `h`, centred on `0.5`.
    pub paper_height: Vec<f32>,
    /// Current bleed mask `m` from `Operation::SetMask`; `1` everywhere when unmasked.
    pub bleed_mask: Vec<f32>,
    /// Evaporation multiplier from the latest `Operation::Dry`.
    pub dry_rate: f32,
    /// Share of the remaining film each tick takes, from the latest
    /// `Operation::Settle`; `0` until one is applied.
    pub settle_share: f32,
    /// How the renderer turns this state into pixels. Carried in the state
    /// (and its checkpoints) so every backend reads it from the same place;
    /// see `Scene::composite_mode`.
    pub composite_mode: CompositeMode,
    /// Output aspect the grid is stretched to; stamps and masks are measured
    /// in its isotropic metric (`scene::isotropic_scale`). Taken from the
    /// paper field the grid was built from.
    pub aspect: f32,
    /// Ticks stepped since load. The swirl noise drifts with it, so it is
    /// state a checkpoint carries rather than a clock.
    pub tick: u32,
    /// Swirl noise seed, below `2^24` so it packs exactly into an `f32`.
    pub swirl_seed: u32,
}

impl SimulationGrid {
    pub fn new(paper: &PaperField, pigment_count: usize) -> SimulationGrid {
        let n = paper.len();
        SimulationGrid {
            width: paper.width,
            height: paper.height_px,
            pigment_count,
            wet: vec![0.0; n],
            velocity_u: vec![0.0; n],
            velocity_v: vec![0.0; n],
            pressure: vec![0.0; n],
            pigments_in_water: vec![0.0; n * pigment_count],
            pigments_deposited: vec![0.0; n * pigment_count],
            saturation: vec![0.0; n],
            capacity: paper.capacity.clone(),
            paper_height: paper.height.clone(),
            bleed_mask: vec![1.0; n],
            dry_rate: 1.0,
            settle_share: 0.0,
            composite_mode: CompositeMode::Subtractive,
            aspect: paper.aspect,
            tick: 0,
            swirl_seed: 0,
        }
    }

    pub fn with_aspect(mut self, aspect: f32) -> SimulationGrid {
        self.aspect = aspect;
        self
    }

    /// Seeds the swirl noise from the scene seed.
    pub fn with_swirl_seed(mut self, seed: Seed) -> SimulationGrid {
        self.swirl_seed = (seed.derive(SubSeed::Swirl).0 & 0x00FF_FFFF) as u32;
        self
    }

    pub fn with_composite_mode(mut self, mode: CompositeMode) -> SimulationGrid {
        self.composite_mode = mode;
        self
    }

    pub fn cell_count(&self) -> usize {
        (self.width as usize) * (self.height as usize)
    }

    pub fn index(&self, x: u32, y: u32) -> usize {
        (y as usize) * (self.width as usize) + x as usize
    }

    pub fn in_water(&self, pigment: usize, cell: usize) -> f32 {
        self.pigments_in_water[pigment * self.cell_count() + cell]
    }

    pub fn deposited(&self, pigment: usize, cell: usize) -> f32 {
        self.pigments_deposited[pigment * self.cell_count() + cell]
    }

    /// Total water depth across the grid.
    pub fn total_water(&self) -> f32 {
        self.pressure.iter().sum()
    }

    /// Total pigment, suspended plus deposited.
    pub fn total_pigment(&self) -> f32 {
        self.pigments_in_water.iter().sum::<f32>() + self.pigments_deposited.iter().sum::<f32>()
    }

    pub fn is_dry(&self) -> bool {
        self.wet.iter().all(|&w| w == 0.0)
    }

    /// `true` if every field is finite.
    pub fn is_finite(&self) -> bool {
        [
            &self.wet,
            &self.velocity_u,
            &self.velocity_v,
            &self.pressure,
            &self.pigments_in_water,
            &self.pigments_deposited,
            &self.saturation,
            &self.bleed_mask,
        ]
        .iter()
        .all(|f| f.iter().all(|v| v.is_finite()))
    }
}
