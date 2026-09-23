//! The reference backend: `domain::sim` and `domain::optics` behind the ports.

use crate::domain::optics::{self, RenderParams};
use crate::domain::paper::render_pixel_scale;
use crate::domain::sim::{self, PigmentCoefficients, Scratch, SimParams};
use crate::domain::{Image, Operation, Palette, Paper, PaperField, Scene, Seed, SimulationGrid};

use super::ports::{CheckpointId, EngineError, Renderer, Simulator};

/// Bytes of checkpoint storage the CPU engine will hold per scene.
pub const CHECKPOINT_BUDGET_BYTES: usize = 256 * 1024 * 1024;

pub struct CpuEngine {
    params: SimParams,
    render_params: RenderParams,
    loaded: Option<Loaded>,
    checkpoints: Vec<(CheckpointId, SimulationGrid)>,
    next_id: u64,
    checkpoint_budget: usize,
}

struct Loaded {
    grid: SimulationGrid,
    palette: Palette,
    paper: Paper,
    coefficients: Vec<PigmentCoefficients>,
    scratch: Scratch,
    paper_cache: Option<PaperField>,
}

impl Default for CpuEngine {
    fn default() -> Self {
        Self::new(SimParams::default(), RenderParams::default())
    }
}

impl CpuEngine {
    pub fn new(params: SimParams, render_params: RenderParams) -> CpuEngine {
        CpuEngine {
            params,
            render_params,
            loaded: None,
            checkpoints: Vec::new(),
            next_id: 1,
            checkpoint_budget: CHECKPOINT_BUDGET_BYTES,
        }
    }

    /// Lowers (or raises) the memory this engine may spend on checkpoints;
    /// takes effect on the next `snapshot`. A budget below one checkpoint
    /// still keeps one, so a seek restores tick 0 and replays from there.
    pub fn with_checkpoint_budget(mut self, bytes: usize) -> Self {
        self.checkpoint_budget = bytes;
        self
    }

    pub fn params(&self) -> &SimParams {
        &self.params
    }

    pub fn grid(&self) -> Option<&SimulationGrid> {
        self.loaded.as_ref().map(|l| &l.grid)
    }

    fn loaded_mut(&mut self) -> Result<&mut Loaded, EngineError> {
        self.loaded
            .as_mut()
            .ok_or_else(|| EngineError::new("no scene loaded"))
    }
}

/// Bytes one checkpoint of `grid` occupies.
pub fn checkpoint_bytes(grid: &SimulationGrid) -> usize {
    let n = grid.cell_count();
    (10 + 2 * grid.pigment_count) * n * std::mem::size_of::<f32>()
}

impl Simulator for CpuEngine {
    fn load(&mut self, scene: &Scene) -> Result<(), EngineError> {
        scene
            .validate()
            .map_err(|e| EngineError::new(format!("invalid scene: {e:?}")))?;
        let res = scene.sim_resolution.0;
        let field = PaperField::generate_with_aspect(&scene.paper, res, res, scene.aspect());
        let grid = SimulationGrid::new(&field, scene.palette.len())
            .with_composite_mode(scene.composite_mode());
        let scratch = Scratch::for_grid(&grid);
        self.checkpoints.clear();
        self.loaded = Some(Loaded {
            grid,
            palette: scene.palette.clone(),
            paper: scene.paper,
            coefficients: PigmentCoefficients::from_palette(&scene.palette),
            scratch,
            paper_cache: None,
        });
        Ok(())
    }

    fn apply(&mut self, op: &Operation, seed: Seed) -> Result<(), EngineError> {
        let params = self.params;
        let l = self.loaded_mut()?;
        sim::apply(&mut l.grid, op, &params, seed);
        Ok(())
    }

    fn tick(&mut self) -> Result<(), EngineError> {
        let params = self.params;
        let l = self.loaded_mut()?;
        sim::step(&mut l.grid, &l.coefficients, &params, &mut l.scratch);
        Ok(())
    }

    fn snapshot(&mut self) -> Result<Option<CheckpointId>, EngineError> {
        let capacity = self.checkpoint_capacity();
        let l = self.loaded_mut()?;
        if capacity == 0 {
            return Ok(None);
        }
        let grid = l.grid.clone();
        if self.checkpoints.len() >= capacity {
            return Ok(None);
        }
        let id = CheckpointId(self.next_id);
        self.next_id += 1;
        self.checkpoints.push((id, grid));
        Ok(Some(id))
    }

    fn restore(&mut self, id: CheckpointId) -> Result<(), EngineError> {
        let grid = self
            .checkpoints
            .iter()
            .find(|(cid, _)| *cid == id)
            .map(|(_, g)| g.clone())
            .ok_or_else(|| EngineError::new(format!("unknown checkpoint {id:?}")))?;
        self.loaded_mut()?.grid = grid;
        Ok(())
    }

    fn release(&mut self, id: CheckpointId) {
        self.checkpoints.retain(|(cid, _)| *cid != id);
    }

    fn checkpoint_capacity(&self) -> usize {
        match &self.loaded {
            Some(l) => (self.checkpoint_budget / checkpoint_bytes(&l.grid).max(1)).clamp(1, 64),
            None => 0,
        }
    }
}

impl Renderer for CpuEngine {
    fn render(&mut self, width: u32, height: u32) -> Result<Image, EngineError> {
        if width == 0 || height == 0 {
            return Err(EngineError::new("zero output size"));
        }
        let render_params = self.render_params;
        let l = self.loaded_mut()?;
        let needs_paper =
            !matches!(&l.paper_cache, Some(p) if p.width == width && p.height_px == height);
        if needs_paper {
            let aspect = l.grid.aspect;
            l.paper_cache = Some(PaperField::generate_with_pixel_scale(
                &l.paper,
                width,
                height,
                aspect,
                render_pixel_scale(width, height, aspect),
            ));
        }
        let paper = l.paper_cache.as_ref().expect("paper cache filled above");
        Ok(optics::render(&l.grid, &l.palette, paper, &render_params))
    }
}
