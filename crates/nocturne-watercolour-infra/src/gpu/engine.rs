use std::collections::{HashMap, VecDeque};
use std::future::Future;
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll, Waker};

use bytemuck::{Pod, Zeroable};
#[cfg(not(target_arch = "wasm32"))]
use nocturne_watercolour_core::application::Renderer;
use nocturne_watercolour_core::application::{CheckpointId, EngineError, Simulator};
use nocturne_watercolour_core::domain::optics::RenderParams;
use nocturne_watercolour_core::domain::paint::{self, StampParams, StampTarget, WET_THRESHOLD};
use nocturne_watercolour_core::domain::palette::MAX_PIGMENTS;
use nocturne_watercolour_core::domain::paper::render_pixel_scale;
use nocturne_watercolour_core::domain::sim::{self, PigmentCoefficients, SimParams};
use nocturne_watercolour_core::domain::swirl;
use nocturne_watercolour_core::domain::{
    Image, MAX_SETTLE_SHARE, Operation, Paper, PaperField, Scene, Seed, SimulationGrid, StrokeSpan,
};

use super::context::GpuContext;
use super::layout::StateLayout;
use super::surface::PresentSurface;
use super::timer::GpuTimer;

/// Bytes of checkpoint storage held on the GPU per loaded scene. At the
/// 512x512 / 8-pigment maximum one checkpoint is 25 MB, so this admits 10;
/// at 256x256 with 4 pigments it admits the 64-checkpoint cap.
pub const CHECKPOINT_BUDGET_BYTES: u64 = 256 * 1024 * 1024;
const MAX_CHECKPOINTS: usize = 64;

/// Ticks encoded per command buffer. Windows resets the display driver when
/// one command buffer runs past its two-second watchdog, and a tick at the
/// 512^2 maximum is a few milliseconds on an integrated GPU, so sixteen keeps
/// a submission two orders of magnitude inside that while still amortising
/// the encoder over a replay.
const TICKS_PER_SUBMIT: u32 = 16;

/// Command buffers allowed in flight before a submit blocks on the oldest.
/// Native only: the browser paces its own queue. Bounds the driver memory an
/// unattended replay can pin, and turns a hung device into a timed-out wait
/// instead of an ever-growing queue.
const MAX_IN_FLIGHT_SUBMISSIONS: usize = 32;

/// Output pixels per render dispatch. The optics pass reads 4x4 taps times a
/// 3x3 presence window per pigment per pixel, so its cost grows with the
/// canvas; a larger frame is rendered as row bands, each its own submission,
/// so no single dispatch does.
const RENDER_PIXELS_PER_DISPATCH: u64 = 1 << 20;

const WORKGROUP: u32 = 256;

/// Stroke uniforms one batch can hold (see [`Pending`]), each at its own
/// dynamic offset; a batch with more events is submitted and a new one begun.
const STROKE_SLOTS: u32 = 64;

/// Zero words in a row that an upload into a zero-filled buffer leaves out.
/// A loaded state is mostly fields that start at zero (water, velocity,
/// pigment), so only the paper, the mask and the header go over.
const SKIPPED_ZERO_RUN: usize = 1024;

/// GPU bytes of render-resolution paper an engine and its forks keep after
/// the instance that made them is gone. A remount at the same size (a tab
/// switch, a re-hover, a list of same-seed accents) then skips generating
/// the paper on the CPU, the main-thread cost of a first present; eight
/// megabytes is two 1024^2 canvases or thirty-two 256^2 ones.
const PAPER_CACHE_BYTES: u64 = 8 * 1024 * 1024;

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct ParamsUniform {
    width: u32,
    height: u32,
    pigment_count: u32,
    n: u32,
    slope_gain: f32,
    pressure_gain: f32,
    viscosity: f32,
    drag: f32,
    max_velocity: f32,
    blur_radius: u32,
    flow_outward_eta: f32,
    pigment_diffusion: f32,
    water_diffusion: f32,
    diffusion_depth: f32,
    deposition_rate: f32,
    lift_rate: f32,
    wet_lo: f32,
    wet_hi: f32,
    settle_base: f32,
    dry_deposition: f32,
    settle_curve: f32,
    capillary_absorb: f32,
    capillary_epsilon: f32,
    capillary_sigma: f32,
    capillary_rate: f32,
    capillary_dry: f32,
    wet_capillary_dry: f32,
    capillary_seep: f32,
    evaporation: f32,
    dry_threshold: f32,
    mask_evaporation: f32,
    dt: f32,
    max_water_depth: f32,
    max_suspended: f32,
    wet_threshold: f32,
    _pad: f32,
    wet_settle: f32,
    stain_bite: f32,
    carry: f32,
    lift_still: f32,
    lift_flow_gain: f32,
    max_deposited: f32,
    carry_min: f32,
    carry_reach: f32,
    swirl_drift: f32,
    swirl_depth: f32,
    /// `swirl::Geometry` for the loaded scene's size and aspect.
    swirl_radius_x: u32,
    swirl_radius_y: u32,
    swirl_inv_x: f32,
    swirl_inv_y: f32,
    swirl_step_x: f32,
    swirl_step_y: f32,
    swirl_scale: f32,
    _pad2: f32,
    _pad3: f32,
    _pad4: f32,
}

impl ParamsUniform {
    fn new(width: u32, height: u32, pigment_count: u32, aspect: f32, p: &SimParams) -> Self {
        let geo = swirl::Geometry::new(width, aspect, p.swirl_speed, p.swirl_frequency);
        ParamsUniform {
            width,
            height,
            pigment_count,
            n: width * height,
            slope_gain: p.slope_gain,
            pressure_gain: p.pressure_gain,
            viscosity: p.viscosity,
            drag: p.drag,
            max_velocity: p.max_velocity,
            blur_radius: p.blur_radius,
            flow_outward_eta: p.flow_outward_eta,
            pigment_diffusion: p.pigment_diffusion,
            water_diffusion: p.water_diffusion,
            diffusion_depth: p.diffusion_depth,
            deposition_rate: p.deposition_rate,
            lift_rate: p.lift_rate,
            wet_lo: p.wet_lo,
            wet_hi: p.wet_hi,
            settle_base: p.settle_base,
            dry_deposition: p.dry_deposition,
            settle_curve: p.settle_curve,
            capillary_absorb: p.capillary_absorb,
            capillary_epsilon: p.capillary_epsilon,
            capillary_sigma: p.capillary_sigma,
            capillary_rate: p.capillary_rate,
            capillary_dry: p.capillary_dry,
            wet_capillary_dry: p.wet_capillary_dry,
            capillary_seep: p.capillary_seep,
            evaporation: p.evaporation,
            dry_threshold: p.dry_threshold,
            mask_evaporation: p.mask_evaporation,
            dt: sim::DT,
            max_water_depth: sim::MAX_WATER_DEPTH,
            max_suspended: sim::MAX_SUSPENDED,
            wet_threshold: WET_THRESHOLD,
            _pad: 0.0,
            wet_settle: p.wet_settle,
            stain_bite: p.stain_bite,
            carry: p.carry,
            lift_still: p.lift_still,
            lift_flow_gain: p.lift_flow_gain,
            max_deposited: sim::MAX_DEPOSITED,
            carry_min: sim::CARRY_MIN,
            carry_reach: sim::CARRY_REACH,
            swirl_drift: p.swirl_drift,
            swirl_depth: p.swirl_depth,
            swirl_radius_x: geo.radius_x,
            swirl_radius_y: geo.radius_y,
            swirl_inv_x: geo.inv_x,
            swirl_inv_y: geo.inv_y,
            swirl_step_x: geo.step_x,
            swirl_step_y: geo.step_y,
            swirl_scale: geo.scale,
            _pad2: 0.0,
            _pad3: 0.0,
            _pad4: 0.0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct StrokeUniform {
    kind: u32,
    pigment: u32,
    /// The laydown flow, computed on the CPU by `paint::stroke_flow` so both
    /// engines inject the same velocity; see `paint::StrokeFlow`.
    kick_x: f32,
    kick_y: f32,
    concentration: f32,
    water: f32,
    strength: f32,
    splat_out: f32,
    rect_x: u32,
    rect_y: u32,
    rect_w: u32,
    rect_h: u32,
    /// Word offset of this stroke's rect in the stamp arena.
    stamp_offset: u32,
    _pad: [u32; 3],
}

/// The rows and columns of a stamp that hold a non-zero coverage bit, with
/// that rect's coverage row-major: all an apply pass needs to see, since a
/// zero-coverage cell leaves the grid as it was.
struct StampRect {
    x: u32,
    y: u32,
    w: u32,
    h: u32,
    coverage: Vec<f32>,
}

impl StampRect {
    fn of(stamp: &paint::Stamp) -> Option<StampRect> {
        let width = stamp.width as usize;
        let (mut x0, mut x1, mut y0, mut y1) = (usize::MAX, 0, usize::MAX, 0);
        for (y, row) in stamp.coverage.chunks_exact(width.max(1)).enumerate() {
            let Some(first) = row.iter().position(|c| c.to_bits() != 0) else {
                continue;
            };
            let last = row.iter().rposition(|c| c.to_bits() != 0).unwrap_or(first);
            x0 = x0.min(first);
            x1 = x1.max(last + 1);
            y0 = y0.min(y);
            y1 = y + 1;
        }
        if y0 == usize::MAX {
            return None;
        }
        let mut coverage = Vec::with_capacity((x1 - x0) * (y1 - y0));
        for y in y0..y1 {
            coverage.extend_from_slice(&stamp.coverage[y * width + x0..y * width + x1]);
        }
        Some(StampRect {
            x: x0 as u32,
            y: y0 as u32,
            w: (x1 - x0) as u32,
            h: (y1 - y0) as u32,
            coverage,
        })
    }
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct PigmentCoefUniform {
    density: f32,
    staining_power: f32,
    granulation: f32,
    _pad: f32,
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct RenderUniform {
    out_width: u32,
    out_height: u32,
    sim_width: u32,
    sim_height: u32,
    pigment_count: u32,
    n: u32,
    /// First output row of this dispatch's band.
    y_offset: u32,
    encode_srgb: u32,
    granulation_gain: f32,
    wet_pigment_visibility: f32,
    thickness_scale: f32,
    wet_darken: f32,
    wet_sheen_add: f32,
    sheen_depth: f32,
    wet_scatter_loss: f32,
    wet_absorb_gain: f32,
    optical_gamma: f32,
    optical_max: f32,
    optical_mid: f32,
    surface_k1: f32,
    surface_k2: f32,
    surface_coverage_gain: f32,
    _p2: f32,
    _p3: f32,
}

#[derive(Clone)]
struct SimPipelines {
    layout: wgpu::BindGroupLayout,
    velocity: wgpu::ComputePipeline,
    divergence: wgpu::ComputePipeline,
    jacobi_a: wgpu::ComputePipeline,
    jacobi_b: wgpu::ComputePipeline,
    project: wgpu::ComputePipeline,
    project_q2: wgpu::ComputePipeline,
    blur_h: wgpu::ComputePipeline,
    blur_v: wgpu::ComputePipeline,
    advect: wgpu::ComputePipeline,
    swirl_distance_h: wgpu::ComputePipeline,
    swirl_distance_v: wgpu::ComputePipeline,
    swirl_stream: wgpu::ComputePipeline,
    swirl_from_scratch: wgpu::ComputePipeline,
    swirl_from_state: wgpu::ComputePipeline,
    transfer: wgpu::ComputePipeline,
    transfer_g_scratch: wgpu::ComputePipeline,
    capillary: wgpu::ComputePipeline,
    capillary_wet: wgpu::ComputePipeline,
    apply_brush: wgpu::ComputePipeline,
    apply_water: wgpu::ComputePipeline,
    apply_lift: wgpu::ComputePipeline,
    dry_all: wgpu::ComputePipeline,
}

#[derive(Clone)]
struct RenderPipeline {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::ComputePipeline,
    /// `presence_taps`: group 0 is the uniform and state, group 1 the
    /// presence buffer it writes.
    presence_inputs: wgpu::BindGroupLayout,
    presence_output: wgpu::BindGroupLayout,
    presence: wgpu::ComputePipeline,
}

/// `fs_render` in `render.wgsl`. The swapchain format is only known once a
/// surface exists, so a pipeline is built on the first present in each
/// format; the cache is shared by every fork, so instances after the first
/// reuse it.
#[derive(Clone)]
struct PresentPipeline {
    layout: wgpu::BindGroupLayout,
    pipeline_layout: wgpu::PipelineLayout,
    module: wgpu::ShaderModule,
    cached: Arc<Mutex<Vec<(wgpu::TextureFormat, wgpu::RenderPipeline)>>>,
}

/// What a render-resolution paper field is a function of.
#[derive(Clone, Copy, PartialEq, Eq)]
struct PaperKey {
    seed: u64,
    paper: [u32; 5],
    width: u32,
    height: u32,
    aspect: u32,
    pixel_scale: u32,
}

impl PaperKey {
    fn new(paper: &Paper, width: u32, height: u32, aspect: f32, pixel_scale: f32) -> PaperKey {
        PaperKey {
            seed: paper.seed.0,
            paper: [
                paper.grain_scale.to_bits(),
                paper.height_amplitude.to_bits(),
                paper.absorbency[0].to_bits(),
                paper.absorbency[1].to_bits(),
                paper.fibre_anisotropy.to_bits(),
            ],
            width,
            height,
            aspect: aspect.to_bits(),
            pixel_scale: pixel_scale.to_bits(),
        }
    }
}

/// Least recently used first; see [`PAPER_CACHE_BYTES`].
type PaperCache = Arc<Mutex<VecDeque<(PaperKey, wgpu::Buffer)>>>;

struct Loaded {
    width: u32,
    height: u32,
    layout: StateLayout,
    paper: Paper,
    paper_sim: PaperField,
    state: wgpu::Buffer,
    stamp: wgpu::Buffer,
    stroke: wgpu::Buffer,
    optics: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
    checkpoints: HashMap<CheckpointId, wgpu::Buffer>,
    render_cache: Option<RenderTarget>,
    /// `swirl::Geometry::substeps` for this scene.
    swirl_substeps: u32,
    /// `false` only while the host knows no cell is wet (after load, after
    /// `DryAll`), so the swirl passes can be skipped without a readback.
    maybe_wet: bool,
}

struct RenderTarget {
    width: u32,
    height: u32,
    pixel_scale: f32,
    uniform: wgpu::Buffer,
    paper: wgpu::Buffer,
    presence: wgpu::Buffer,
    presence_inputs: wgpu::BindGroup,
    presence_output: wgpu::BindGroup,
    present_bind_group: wgpu::BindGroup,
    readback: Option<Readback>,
}

/// The f32 frame `render` writes (16 B per output pixel) and its
/// host-visible copy; see `GpuEngine::ensure_readback`.
struct Readback {
    out: wgpu::Buffer,
    staging: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
}

/// What the simulation encodes between two points something reads the
/// state (a present, a readback, an upload into `state`): ticks, applies
/// and checkpoint copies, submitted as one command buffer. Each apply's
/// stamp rect and stroke uniform get their own region of the stamp arena
/// and of the stroke buffer, since every upload lands before the batch runs.
struct Pending {
    enc: wgpu::CommandEncoder,
    ticks: u32,
    stamp_words: usize,
    strokes: u32,
    /// The tick timer's resolve is in `enc`; its map is requested once the
    /// batch is submitted.
    tick_sample: bool,
}

/// GPU commands encoded since the engine was created or the counts were
/// last reset: what the per-command overhead of a host scales with.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CommandCounts {
    pub passes: u64,
    pub dispatches: u64,
    /// Buffer-to-buffer copies and clears.
    pub copies: u64,
    pub submits: u64,
    /// Bytes handed to `write_buffer`.
    pub uploaded_bytes: u64,
}

#[derive(Default)]
struct CommandCounter {
    passes: AtomicU64,
    dispatches: AtomicU64,
    copies: AtomicU64,
    submits: AtomicU64,
    uploaded_bytes: AtomicU64,
}

impl CommandCounter {
    fn add(counter: &AtomicU64, n: u64) {
        counter.fetch_add(n, Ordering::Relaxed);
    }

    fn get(&self) -> CommandCounts {
        CommandCounts {
            passes: self.passes.load(Ordering::Relaxed),
            dispatches: self.dispatches.load(Ordering::Relaxed),
            copies: self.copies.load(Ordering::Relaxed),
            submits: self.submits.load(Ordering::Relaxed),
            uploaded_bytes: self.uploaded_bytes.load(Ordering::Relaxed),
        }
    }
}

/// GPU time of the latest sampled work, from timestamp queries; `None`
/// until a sample has been read back, and always on a device without
/// `TIMESTAMP_QUERY`. Samples are taken whenever the previous one has been
/// read, so these lag the work by a frame or two.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct GpuTimings {
    /// Milliseconds per tick, averaged over the ticks of one `step`.
    pub tick_ms: Option<f64>,
    /// Milliseconds for one frame's optics (presence pass through the last
    /// band), presented or read back.
    pub render_ms: Option<f64>,
}

struct Timers {
    tick: GpuTimer,
    render: GpuTimer,
    /// Nanoseconds per timestamp unit.
    period: f32,
    /// Ticks the in-flight tick sample spans.
    sampled_ticks: u32,
    last: GpuTimings,
}

impl Timers {
    fn new(ctx: &GpuContext) -> Option<Timers> {
        ctx.has_timestamps().then(|| Timers {
            tick: GpuTimer::new(ctx.device(), "tick-timestamps"),
            render: GpuTimer::new(ctx.device(), "render-timestamps"),
            period: ctx.queue().get_timestamp_period(),
            sampled_ticks: 1,
            last: GpuTimings::default(),
        })
    }

    fn collect(&mut self) {
        if let Some(ms) = self.tick.collect(self.period) {
            self.last.tick_ms = Some(ms / f64::from(self.sampled_ticks.max(1)));
        }
        if let Some(ms) = self.render.collect(self.period) {
            self.last.render_ms = Some(ms);
        }
    }
}

pub struct GpuEngine {
    ctx: GpuContext,
    params: SimParams,
    render_params: RenderParams,
    sim: SimPipelines,
    render: RenderPipeline,
    present: PresentPipeline,
    loaded: Option<Loaded>,
    next_checkpoint: u64,
    checkpoint_budget: u64,
    /// Submissions not yet known to have completed, oldest first; see
    /// [`MAX_IN_FLIGHT_SUBMISSIONS`].
    in_flight: Mutex<VecDeque<wgpu::SubmissionIndex>>,
    pending: Mutex<Option<Pending>>,
    counter: CommandCounter,
    paper_cache: PaperCache,
    timers: Option<Timers>,
}

const COMMON: &str = include_str!("shaders/common.wgsl");
const SIM_SOURCES: [&str; 6] = [
    include_str!("shaders/velocity.wgsl"),
    include_str!("shaders/pressure.wgsl"),
    include_str!("shaders/flow.wgsl"),
    include_str!("shaders/transfer.wgsl"),
    include_str!("shaders/capillary.wgsl"),
    include_str!("shaders/apply.wgsl"),
];
const RENDER_SOURCE: &str = include_str!("shaders/render.wgsl");

fn storage_entry(binding: u32, read_only: bool) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Storage { read_only },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    }
}

fn uniform_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    }
}

fn groups(n: u32) -> u32 {
    n.div_ceil(WORKGROUP)
}

/// Rows per render band: whole 16-row workgroups holding at most
/// [`RENDER_PIXELS_PER_DISPATCH`] pixels, never fewer than one workgroup.
fn render_band_rows(width: u32, height: u32) -> u32 {
    let rows = (RENDER_PIXELS_PER_DISPATCH / u64::from(width.max(1))).min(u64::from(height));
    let whole_groups = (rows as u32 / 16) * 16;
    whole_groups.max(16).min(height.max(1))
}

impl GpuEngine {
    #[cfg(not(target_arch = "wasm32"))]
    pub fn new(ctx: GpuContext) -> Result<GpuEngine, EngineError> {
        Self::with_params(ctx, SimParams::default(), RenderParams::default())
    }

    /// Blocks on shader validation; unavailable in the browser, where the
    /// error scope resolves through a promise (see `with_params_async`).
    #[cfg(not(target_arch = "wasm32"))]
    pub fn with_params(
        ctx: GpuContext,
        params: SimParams,
        render_params: RenderParams,
    ) -> Result<GpuEngine, EngineError> {
        pollster::block_on(Self::with_params_async(ctx, params, render_params))
    }

    pub async fn new_async(ctx: GpuContext) -> Result<GpuEngine, EngineError> {
        Self::with_params_async(ctx, SimParams::default(), RenderParams::default()).await
    }

    pub async fn with_params_async(
        ctx: GpuContext,
        params: SimParams,
        render_params: RenderParams,
    ) -> Result<GpuEngine, EngineError> {
        let (engine, validation) = Self::build(ctx, params, render_params);
        if let Some(err) = validation.await {
            return Err(EngineError::new(format!(
                "shader/pipeline validation: {err}"
            )));
        }
        Ok(engine)
    }

    /// Creates every pipeline inside one validation scope and hands back the
    /// scope's pending result, so sync and async constructors share the work.
    fn build(
        ctx: GpuContext,
        params: SimParams,
        render_params: RenderParams,
    ) -> (GpuEngine, impl Future<Output = Option<wgpu::Error>>) {
        let device = ctx.device();
        let error_scope = device.push_error_scope(wgpu::ErrorFilter::Validation);

        let mut sim_source = String::from(COMMON);
        for s in SIM_SOURCES {
            sim_source.push('\n');
            sim_source.push_str(s);
        }
        let sim_module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("watercolour-sim"),
            source: wgpu::ShaderSource::Wgsl(sim_source.into()),
        });
        let sim_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("watercolour-sim"),
            entries: &[
                uniform_entry(0),
                storage_entry(1, false),
                storage_entry(2, false),
                storage_entry(3, true),
                wgpu::BindGroupLayoutEntry {
                    binding: 4,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: true,
                        min_binding_size: None,
                    },
                    count: None,
                },
                storage_entry(5, true),
            ],
        });
        let sim_pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("watercolour-sim"),
            bind_group_layouts: &[Some(&sim_layout)],
            ..Default::default()
        });
        let make = |entry: &str| {
            device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some(entry),
                layout: Some(&sim_pipeline_layout),
                module: &sim_module,
                entry_point: Some(entry),
                compilation_options: Default::default(),
                cache: None,
            })
        };
        let sim = SimPipelines {
            velocity: make("velocity"),
            divergence: make("divergence"),
            jacobi_a: make("jacobi_a"),
            jacobi_b: make("jacobi_b"),
            project: make("project"),
            project_q2: make("project_q2"),
            blur_h: make("blur_h"),
            blur_v: make("blur_v"),
            advect: make("advect"),
            swirl_distance_h: make("swirl_distance_h"),
            swirl_distance_v: make("swirl_distance_v"),
            swirl_stream: make("swirl_stream"),
            swirl_from_scratch: make("swirl_from_scratch"),
            swirl_from_state: make("swirl_from_state"),
            transfer: make("transfer"),
            transfer_g_scratch: make("transfer_g_scratch"),
            capillary: make("capillary"),
            capillary_wet: make("capillary_wet"),
            apply_brush: make("apply_brush"),
            apply_water: make("apply_water"),
            apply_lift: make("apply_lift"),
            dry_all: make("dry_all"),
            layout: sim_layout,
        };

        let render_module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("watercolour-render"),
            source: wgpu::ShaderSource::Wgsl(RENDER_SOURCE.into()),
        });
        let render_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("watercolour-render"),
            entries: &[
                uniform_entry(0),
                storage_entry(1, true),
                storage_entry(2, true),
                storage_entry(3, true),
                storage_entry(4, false),
                storage_entry(5, true),
            ],
        });
        let render_pipeline_layout =
            device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("watercolour-render"),
                bind_group_layouts: &[Some(&render_layout)],
                ..Default::default()
            });
        let render_pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("render"),
            layout: Some(&render_pipeline_layout),
            module: &render_module,
            entry_point: Some("render"),
            compilation_options: Default::default(),
            cache: None,
        });
        let presence_inputs = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("watercolour-presence-inputs"),
            entries: &[uniform_entry(0), storage_entry(1, true)],
        });
        let presence_output = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("watercolour-presence-output"),
            entries: &[storage_entry(0, false)],
        });
        let presence_pipeline_layout =
            device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("watercolour-presence"),
                bind_group_layouts: &[Some(&presence_inputs), Some(&presence_output)],
                ..Default::default()
            });
        let presence_pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("presence_taps"),
            layout: Some(&presence_pipeline_layout),
            module: &render_module,
            entry_point: Some("presence_taps"),
            compilation_options: Default::default(),
            cache: None,
        });

        let fragment = |binding: u32, ty: wgpu::BufferBindingType| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Buffer {
                ty,
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        };
        let read_only = wgpu::BufferBindingType::Storage { read_only: true };
        let present_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("watercolour-present"),
            entries: &[
                fragment(0, wgpu::BufferBindingType::Uniform),
                fragment(1, read_only),
                fragment(2, read_only),
                fragment(3, read_only),
                fragment(5, read_only),
            ],
        });
        let present_pipeline_layout =
            device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("watercolour-present"),
                bind_group_layouts: &[Some(&present_layout)],
                ..Default::default()
            });

        let validation = error_scope.pop();
        let timers = Timers::new(&ctx);
        let engine = GpuEngine {
            ctx,
            params,
            render_params,
            sim,
            render: RenderPipeline {
                layout: render_layout,
                pipeline: render_pipeline,
                presence_inputs,
                presence_output,
                presence: presence_pipeline,
            },
            present: PresentPipeline {
                layout: present_layout,
                pipeline_layout: present_pipeline_layout,
                module: render_module,
                cached: Arc::default(),
            },
            loaded: None,
            next_checkpoint: 1,
            checkpoint_budget: CHECKPOINT_BUDGET_BYTES,
            in_flight: Mutex::new(VecDeque::new()),
            pending: Mutex::new(None),
            counter: CommandCounter::default(),
            paper_cache: Arc::default(),
            timers,
        };
        (engine, validation)
    }

    /// A second engine on the same device sharing this one's compiled
    /// pipelines (wgpu handles are reference counted), with no scene loaded.
    /// Shader compilation is the expensive part of construction, so hosts
    /// running several scenes build one engine and fork it.
    pub fn fork(&self) -> GpuEngine {
        GpuEngine {
            ctx: self.ctx.clone(),
            params: self.params,
            render_params: self.render_params,
            sim: self.sim.clone(),
            render: self.render.clone(),
            present: self.present.clone(),
            loaded: None,
            next_checkpoint: 1,
            checkpoint_budget: self.checkpoint_budget,
            in_flight: Mutex::new(VecDeque::new()),
            pending: Mutex::new(None),
            counter: CommandCounter::default(),
            paper_cache: Arc::clone(&self.paper_cache),
            timers: Timers::new(&self.ctx),
        }
    }

    /// Lowers (or raises) the GPU memory this engine may spend on
    /// checkpoints; takes effect on the next `load`. Browsers share one
    /// device between several instances, so the wasm adapter sets a fraction
    /// of [`CHECKPOINT_BUDGET_BYTES`].
    pub fn with_checkpoint_budget(mut self, bytes: u64) -> Self {
        self.checkpoint_budget = bytes;
        self
    }

    /// Bytes currently held by checkpoint buffers for the loaded scene.
    pub fn checkpoint_bytes(&self) -> u64 {
        match &self.loaded {
            Some(l) => l.checkpoints.len() as u64 * l.layout.state_bytes(),
            None => 0,
        }
    }

    pub fn command_counts(&self) -> CommandCounts {
        self.counter.get()
    }

    /// The latest GPU timestamps read back, collecting any that have
    /// arrived; never blocks.
    pub fn gpu_timings(&mut self) -> GpuTimings {
        #[cfg(not(target_arch = "wasm32"))]
        let _ = self.ctx.device().poll(wgpu::PollType::Poll);
        match self.timers.as_mut() {
            Some(t) => {
                t.collect();
                t.last
            }
            None => GpuTimings::default(),
        }
    }

    pub fn context(&self) -> &GpuContext {
        &self.ctx
    }

    /// Blocks until every submitted command has finished; used for timing.
    pub fn sync(&self) -> Result<(), EngineError> {
        self.flush()?;
        self.ctx.wait_idle()
    }

    fn loaded(&self) -> Result<&Loaded, EngineError> {
        self.loaded
            .as_ref()
            .ok_or_else(|| EngineError::new("no scene loaded"))
    }

    fn loaded_mut(&mut self) -> Result<&mut Loaded, EngineError> {
        self.loaded
            .as_mut()
            .ok_or_else(|| EngineError::new("no scene loaded"))
    }

    fn render_target(&self) -> Result<&RenderTarget, EngineError> {
        self.loaded()?
            .render_cache
            .as_ref()
            .ok_or_else(|| EngineError::new("no rendered frame"))
    }

    /// Allocates a buffer, refusing one the device could not bind: wgpu
    /// reports an oversized buffer as an uncaptured error after the fact,
    /// which would fault the whole device for every instance sharing it.
    fn buffer(
        &self,
        label: &str,
        size: u64,
        usage: wgpu::BufferUsages,
    ) -> Result<wgpu::Buffer, EngineError> {
        let limits = self.ctx.limits();
        let size = size.max(16);
        let cap = if usage.contains(wgpu::BufferUsages::STORAGE) {
            limits
                .max_buffer_size
                .min(limits.max_storage_buffer_binding_size)
        } else {
            limits.max_buffer_size
        };
        if size > cap {
            return Err(EngineError::new(format!(
                "{label}: {size} bytes exceeds the device limit of {cap}"
            )));
        }
        Ok(self.ctx.device().create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size,
            usage,
            mapped_at_creation: false,
        }))
    }

    /// The one path every command buffer takes. Refuses a lost or faulted
    /// device, and natively blocks on the oldest outstanding submission once
    /// [`MAX_IN_FLIGHT_SUBMISSIONS`] are pending.
    fn submit(&self, commands: wgpu::CommandBuffer) -> Result<(), EngineError> {
        self.ctx.check()?;
        CommandCounter::add(&self.counter.submits, 1);
        let index = self.ctx.queue().submit([commands]);
        let oldest = {
            let mut pending = self.in_flight.lock().unwrap_or_else(|p| p.into_inner());
            pending.push_back(index);
            if pending.len() > MAX_IN_FLIGHT_SUBMISSIONS {
                pending.pop_front()
            } else {
                None
            }
        };
        #[cfg(not(target_arch = "wasm32"))]
        if let Some(oldest) = oldest {
            self.ctx.wait_for(oldest)?;
        }
        #[cfg(target_arch = "wasm32")]
        let _ = oldest;
        Ok(())
    }

    /// Runs `f` on the open batch, beginning one if there is none.
    fn with_pending<R>(&self, f: impl FnOnce(&mut Pending) -> R) -> R {
        let mut slot = self.pending.lock().unwrap_or_else(|p| p.into_inner());
        let pending = slot.get_or_insert_with(|| Pending {
            enc: self
                .ctx
                .device()
                .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                    label: Some("batch"),
                }),
            ticks: 0,
            stamp_words: 0,
            strokes: 0,
            tick_sample: false,
        });
        f(pending)
    }

    fn take_pending(&self) -> Option<Pending> {
        self.pending
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .take()
    }

    /// Submits the open batch, if any.
    pub fn flush(&self) -> Result<(), EngineError> {
        match self.take_pending() {
            Some(pending) => self.submit_batch(pending.enc, pending.tick_sample),
            None => Ok(()),
        }
    }

    /// Submits `enc`, then requests the tick sample it resolved, if any.
    fn submit_batch(
        &self,
        enc: wgpu::CommandEncoder,
        tick_sample: bool,
    ) -> Result<(), EngineError> {
        self.submit(enc.finish())?;
        if tick_sample && let Some(t) = &self.timers {
            t.tick.request();
        }
        Ok(())
    }

    /// Byte offset of stroke slot `slot` in the stroke buffer.
    fn stroke_offset(&self, slot: u32) -> u32 {
        slot * self.ctx.limits().min_uniform_buffer_offset_alignment
    }

    fn write_buffer(&self, buffer: &wgpu::Buffer, offset: u64, data: &[u8]) {
        CommandCounter::add(&self.counter.uploaded_bytes, data.len() as u64);
        self.ctx.queue().write_buffer(buffer, offset, data);
    }

    /// Uploads `data` into a buffer that is still zero-filled, leaving out
    /// its runs of zero words.
    fn write_into_zeroed(&self, buffer: &wgpu::Buffer, data: &[f32]) {
        let f = std::mem::size_of::<f32>();
        let mut start = 0;
        let mut i = 0;
        while i < data.len() {
            if data[i].to_bits() != 0 {
                i += 1;
                continue;
            }
            let run = i;
            while i < data.len() && data[i].to_bits() == 0 {
                i += 1;
            }
            if i - run >= SKIPPED_ZERO_RUN {
                if run > start {
                    self.write_buffer(
                        buffer,
                        (start * f) as u64,
                        bytemuck::cast_slice(&data[start..run]),
                    );
                }
                start = i;
            }
        }
        if start < data.len() {
            self.write_buffer(
                buffer,
                (start * f) as u64,
                bytemuck::cast_slice(&data[start..]),
            );
        }
    }

    /// Encodes one tick into `pass`: the same pass order as `sim::step`.
    /// Every dispatch in a compute pass is its own usage scope, so each one
    /// sees the storage writes of the one before; where the CPU swaps
    /// vectors, the next reader takes the field from `scratch` instead (see
    /// `common.wgsl`), so a tick needs no copies.
    fn encode_tick(&self, pass: &mut wgpu::ComputePass<'_>, l: &Loaded) {
        let cells = groups(l.layout.n as u32);
        let mut dispatches = 0;
        let mut dispatch = |pipeline: &wgpu::ComputePipeline, groups: u32| {
            pass.set_pipeline(pipeline);
            pass.dispatch_workgroups(groups, 1, 1);
            dispatches += 1;
        };
        let sim = &self.sim;

        dispatch(&sim.velocity, cells);
        dispatch(&sim.divergence, cells);
        for i in 0..self.params.jacobi_iterations {
            dispatch(
                if i % 2 == 0 {
                    &sim.jacobi_a
                } else {
                    &sim.jacobi_b
                },
                cells,
            );
        }
        if self.params.jacobi_iterations % 2 == 1 {
            dispatch(&sim.project_q2, cells);
        } else {
            dispatch(&sim.project, cells);
        }

        dispatch(&sim.blur_h, cells);
        dispatch(&sim.blur_v, cells);
        dispatch(&sim.advect, cells);

        let mut g_in_scratch = true;
        if self.params.swirl_speed > 0.0 && l.maybe_wet {
            dispatch(&sim.swirl_distance_h, cells);
            dispatch(&sim.swirl_distance_v, cells);
            dispatch(&sim.swirl_stream, groups(l.layout.corner_count() as u32));
            for _ in 0..l.swirl_substeps {
                dispatch(
                    if g_in_scratch {
                        &sim.swirl_from_scratch
                    } else {
                        &sim.swirl_from_state
                    },
                    cells,
                );
                g_in_scratch = !g_in_scratch;
            }
        }

        if g_in_scratch {
            dispatch(&sim.transfer_g_scratch, cells);
        } else {
            dispatch(&sim.transfer, cells);
        }
        dispatch(&sim.capillary, cells);
        dispatch(&sim.capillary_wet, cells);
        CommandCounter::add(&self.counter.dispatches, dispatches);
    }

    /// Writes into `state` directly, so the batch encoded so far is submitted
    /// first: an upload lands ahead of any command buffer not yet submitted.
    fn write_state_region(&self, offset_elems: usize, data: &[f32]) -> Result<(), EngineError> {
        self.flush()?;
        let l = self.loaded()?;
        self.write_buffer(
            &l.state,
            (offset_elems * std::mem::size_of::<f32>()) as u64,
            bytemuck::cast_slice(data),
        );
        Ok(())
    }

    /// Encodes `pipeline` over `cells` into the batch, reading stroke slot
    /// `slot`.
    fn dispatch_apply(
        &self,
        pipeline: &wgpu::ComputePipeline,
        cells: u32,
        slot: u32,
    ) -> Result<(), EngineError> {
        let l = self.loaded()?;
        let offset = self.stroke_offset(slot);
        self.with_pending(|p| {
            let mut pass = p
                .enc
                .begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &l.bind_group, &[offset]);
            pass.dispatch_workgroups(groups(cells), 1, 1);
        });
        CommandCounter::add(&self.counter.passes, 1);
        CommandCounter::add(&self.counter.dispatches, 1);
        Ok(())
    }

    /// Uploads the stamp's non-zero rect and applies `pipeline` over it; a
    /// stamp with no coverage changes nothing and is skipped.
    fn apply_stamp(
        &self,
        pipeline: &wgpu::ComputePipeline,
        stamp: &paint::Stamp,
        stroke: StrokeUniform,
    ) -> Result<(), EngineError> {
        let Some(rect) = StampRect::of(stamp) else {
            return Ok(());
        };
        let arena = self.loaded()?.layout.n;
        let words = rect.coverage.len();
        let full = self
            .pending
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .as_ref()
            .is_some_and(|p| p.stamp_words + words > arena || p.strokes >= STROKE_SLOTS);
        if full {
            self.flush()?;
        }
        let (offset, slot) = self.with_pending(|p| {
            let at = (p.stamp_words, p.strokes);
            p.stamp_words += words;
            p.strokes += 1;
            at
        });
        let l = self.loaded()?;
        let stroke = StrokeUniform {
            rect_x: rect.x,
            rect_y: rect.y,
            rect_w: rect.w,
            rect_h: rect.h,
            stamp_offset: offset as u32,
            ..stroke
        };
        let f = std::mem::size_of::<f32>();
        self.write_buffer(
            &l.stamp,
            (offset * f) as u64,
            bytemuck::cast_slice(&rect.coverage),
        );
        self.write_buffer(
            &l.stroke,
            u64::from(self.stroke_offset(slot)),
            bytemuck::bytes_of(&stroke),
        );
        self.dispatch_apply(pipeline, rect.w * rect.h, slot)
    }

    /// Reads the whole state back; used by tests and the CPU comparison.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn read_grid(&self) -> Result<SimulationGrid, EngineError> {
        self.flush()?;
        let l = self.loaded()?;
        let bytes = l.layout.state_bytes();
        let staging = self.buffer(
            "state-readback",
            bytes,
            wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        )?;
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("readback"),
            });
        enc.copy_buffer_to_buffer(&l.state, 0, &staging, 0, bytes);
        self.submit(enc.finish())?;
        let data = self.map_read(&staging)?;
        let floats: &[f32] = bytemuck::cast_slice(&data);
        Ok(l.layout.unpack(floats, l.width, l.height))
    }

    #[cfg(not(target_arch = "wasm32"))]
    fn map_read(&self, staging: &wgpu::Buffer) -> Result<Vec<u8>, EngineError> {
        let slice = staging.slice(..);
        let (tx, rx) = std::sync::mpsc::channel();
        slice.map_async(wgpu::MapMode::Read, move |r| {
            let _ = tx.send(r);
        });
        self.ctx.wait_idle()?;
        rx.recv()
            .map_err(|_| EngineError::new("map_async callback dropped"))?
            .map_err(|e| EngineError::new(format!("map_async: {e:?}")))?;
        let data = {
            let view = slice
                .get_mapped_range()
                .map_err(|e| EngineError::new(format!("get_mapped_range: {e}")))?;
            view.to_vec()
        };
        staging.unmap();
        Ok(data)
    }

    /// The readback path a single-threaded host can use: the map callback
    /// completes a future instead of a channel receive.
    async fn map_read_async(&self, staging: &wgpu::Buffer) -> Result<Vec<u8>, EngineError> {
        let slice = staging.slice(..);
        let shared = Arc::new(Mutex::new(MapState::default()));
        let completion = Arc::clone(&shared);
        slice.map_async(wgpu::MapMode::Read, move |r| {
            let mut st = completion.lock().unwrap_or_else(|p| p.into_inner());
            st.result = Some(r.map_err(|e| format!("{e:?}")));
            if let Some(w) = st.waker.take() {
                w.wake();
            }
        });
        // WebGPU polls the device itself; natively the callback fires only
        // from a poll, and blocking here is fine off the browser.
        #[cfg(not(target_arch = "wasm32"))]
        self.ctx.wait_idle()?;
        MapFuture { shared }
            .await
            .map_err(|e| EngineError::new(format!("map_async: {e}")))?;
        let data = {
            let view = slice
                .get_mapped_range()
                .map_err(|e| EngineError::new(format!("get_mapped_range: {e}")))?;
            view.to_vec()
        };
        staging.unmap();
        Ok(data)
    }

    fn ensure_render_target(&mut self, width: u32, height: u32) -> Result<(), EngineError> {
        // `NOCTURNE_PAPER_BANDLIMIT=0` renders the pre-band-limited field, so
        // before/after PNGs can be produced from the same loaded state. The
        // pixel scale is part of the cache key so toggling it rebuilds the
        // paper at the same output size.
        let band_limit = std::env::var("NOCTURNE_PAPER_BANDLIMIT")
            .map(|v| v != "0")
            .unwrap_or(true);
        let needs = {
            let cached = &self.loaded()?.render_cache;
            let (paper, aspect) = {
                let l = self.loaded()?;
                (l.paper, l.paper_sim.aspect)
            };
            let pixel_scale = if band_limit {
                render_pixel_scale(width, height, aspect)
            } else {
                0.0
            };
            let matches = matches!(
                cached,
                Some(t) if t.width == width
                    && t.height == height
                    && (t.pixel_scale - pixel_scale).abs() < 1e-6
            );
            if matches {
                return Ok(());
            }
            (paper, aspect, pixel_scale)
        };
        let (paper, aspect, pixel_scale) = needs;
        let uniform = self.buffer(
            "render-params",
            std::mem::size_of::<RenderUniform>() as u64,
            wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        )?;
        let paper_buf = self.paper_buffer(
            PaperKey::new(&paper, width, height, aspect, pixel_scale),
            &paper,
        )?;
        let (presence_len, pigments) = {
            let l = self.loaded()?;
            (
                (l.width as u64 + 3) * (l.height as u64 + 3),
                l.layout.pigment_count as u64,
            )
        };
        let presence = self.buffer(
            "render-presence",
            presence_len * pigments * 8,
            wgpu::BufferUsages::STORAGE,
        )?;
        let l = self.loaded()?;
        let device = self.ctx.device();
        let presence_inputs = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("watercolour-presence-inputs"),
            layout: &self.render.presence_inputs,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: l.state.as_entire_binding(),
                },
            ],
        });
        let presence_output = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("watercolour-presence-output"),
            layout: &self.render.presence_output,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: presence.as_entire_binding(),
            }],
        });
        let present_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("watercolour-present"),
            layout: &self.present.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: l.state.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: l.optics.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: paper_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: presence.as_entire_binding(),
                },
            ],
        });
        self.loaded_mut()?.render_cache = Some(RenderTarget {
            width,
            height,
            pixel_scale,
            uniform,
            paper: paper_buf,
            presence,
            presence_inputs,
            presence_output,
            present_bind_group,
            readback: None,
        });
        Ok(())
    }

    /// The render-resolution paper for `key`, from the shared cache or
    /// generated and uploaded (and cached when it fits the budget).
    fn paper_buffer(&self, key: PaperKey, paper: &Paper) -> Result<wgpu::Buffer, EngineError> {
        {
            let mut cache = self.paper_cache.lock().unwrap_or_else(|p| p.into_inner());
            if let Some(at) = cache.iter().position(|(k, _)| *k == key) {
                let hit = cache
                    .remove(at)
                    .ok_or_else(|| EngineError::new("paper cache"))?;
                let buffer = hit.1.clone();
                cache.push_back(hit);
                return Ok(buffer);
            }
        }
        let field = PaperField::generate_with_pixel_scale(
            paper,
            key.width,
            key.height,
            f32::from_bits(key.aspect),
            f32::from_bits(key.pixel_scale),
        );
        let buffer = self.buffer(
            "paper-out",
            (field.height.len() * std::mem::size_of::<f32>()) as u64,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        )?;
        self.write_buffer(&buffer, 0, bytemuck::cast_slice(&field.height));
        if buffer.size() <= PAPER_CACHE_BYTES {
            let mut cache = self.paper_cache.lock().unwrap_or_else(|p| p.into_inner());
            cache.push_back((key, buffer.clone()));
            let mut total: u64 = cache.iter().map(|(_, b)| b.size()).sum();
            while total > PAPER_CACHE_BYTES {
                match cache.pop_front() {
                    Some((_, evicted)) => total -= evicted.size(),
                    None => break,
                }
            }
        }
        Ok(buffer)
    }

    /// The `out` buffer `render` writes and its host-visible copy, made on
    /// the first readback: a presenting instance never pays for either.
    fn ensure_readback(&mut self) -> Result<(), EngineError> {
        let target = self.render_target()?;
        if target.readback.is_some() {
            return Ok(());
        }
        let bytes = (target.width as u64) * (target.height as u64) * 16;
        let out = self.buffer(
            "render-out",
            bytes,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
        )?;
        let staging = self.buffer(
            "render-staging",
            bytes,
            wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        )?;
        let l = self.loaded()?;
        let target = self.render_target()?;
        let bind_group = self
            .ctx
            .device()
            .create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("watercolour-render"),
                layout: &self.render.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: target.uniform.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: l.state.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: l.optics.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: target.paper.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 4,
                        resource: out.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 5,
                        resource: target.presence.as_entire_binding(),
                    },
                ],
            });
        let target = self
            .loaded_mut()?
            .render_cache
            .as_mut()
            .ok_or_else(|| EngineError::new("no rendered frame"))?;
        target.readback = Some(Readback {
            out,
            staging,
            bind_group,
        });
        Ok(())
    }

    fn render_uniform(
        &self,
        l: &Loaded,
        width: u32,
        height: u32,
        y_offset: u32,
        encode_srgb: bool,
    ) -> RenderUniform {
        RenderUniform {
            out_width: width,
            out_height: height,
            sim_width: l.width,
            sim_height: l.height,
            pigment_count: l.layout.pigment_count as u32,
            n: l.layout.n as u32,
            y_offset,
            encode_srgb: u32::from(encode_srgb),
            granulation_gain: self.render_params.granulation_gain,
            wet_pigment_visibility: self.render_params.wet_pigment_visibility,
            thickness_scale: self.render_params.thickness_scale,
            wet_darken: self.render_params.wet_darken,
            wet_sheen_add: self.render_params.wet_sheen_add,
            sheen_depth: self.render_params.sheen_depth,
            wet_scatter_loss: self.render_params.wet_scatter_loss,
            wet_absorb_gain: self.render_params.wet_absorb_gain,
            optical_gamma: self.render_params.optical_gamma,
            optical_max: self.render_params.optical_max,
            optical_mid: self.render_params.optical_mid,
            surface_k1: self.render_params.surface_k1,
            surface_k2: self.render_params.surface_k2,
            surface_coverage_gain: self.render_params.surface_coverage_gain,
            _p2: 0.0,
            _p3: 0.0,
        }
    }

    fn encode_presence(
        &self,
        enc: &mut wgpu::CommandEncoder,
        l: &Loaded,
        target: &RenderTarget,
        timer: Option<&GpuTimer>,
    ) {
        let taps = (l.width + 3) * (l.height + 3);
        let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("presence"),
            timestamp_writes: timer.map(|t| t.compute_writes(true, false)),
        });
        pass.set_pipeline(&self.render.presence);
        pass.set_bind_group(0, &target.presence_inputs, &[]);
        pass.set_bind_group(1, &target.presence_output, &[]);
        pass.dispatch_workgroups(groups(taps), 1, 1);
        CommandCounter::add(&self.counter.passes, 1);
        CommandCounter::add(&self.counter.dispatches, 1);
    }

    /// Runs the optics pass into the readback buffer at `width` x `height`.
    /// The frame is rendered in row bands (see [`RENDER_PIXELS_PER_DISPATCH`]);
    /// each band's uniform write is queued ahead of its own submission, so
    /// the bands execute in order against the same uniform buffer.
    fn render_frame(&mut self, width: u32, height: u32) -> Result<(), EngineError> {
        if width == 0 || height == 0 {
            return Err(EngineError::new("zero output size"));
        }
        self.flush()?;
        self.ensure_render_target(width, height)?;
        self.ensure_readback()?;
        if let Some(t) = self.timers.as_mut() {
            t.collect();
        }
        let timer = self.render_timer();
        let l = self.loaded()?;
        let target = self.render_target()?;
        let readback = target
            .readback
            .as_ref()
            .ok_or_else(|| EngineError::new("no readback buffer"))?;
        let band_rows = render_band_rows(width, height);
        let mut y_offset = 0;
        while y_offset < height {
            let rows = band_rows.min(height - y_offset);
            let uniform = self.render_uniform(l, width, height, y_offset, false);
            self.write_buffer(&target.uniform, 0, bytemuck::bytes_of(&uniform));
            let mut enc =
                self.ctx
                    .device()
                    .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                        label: Some("render"),
                    });
            if y_offset == 0 {
                self.encode_presence(&mut enc, l, target, timer);
            }
            let last = y_offset + rows >= height;
            {
                let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label: Some("render"),
                    timestamp_writes: timer
                        .filter(|_| last)
                        .map(|t| t.compute_writes(false, true)),
                });
                pass.set_pipeline(&self.render.pipeline);
                pass.set_bind_group(0, &readback.bind_group, &[]);
                pass.dispatch_workgroups(width.div_ceil(16), rows.div_ceil(16), 1);
            }
            CommandCounter::add(&self.counter.passes, 1);
            CommandCounter::add(&self.counter.dispatches, 1);
            if let Some(t) = timer.filter(|_| last) {
                t.resolve(&mut enc);
            }
            self.submit(enc.finish())?;
            y_offset += rows;
        }
        if let Some(t) = timer {
            t.request();
        }
        Ok(())
    }

    /// Shades the current state straight into `view`, a `format` target of
    /// `width` x `height`: the frame `render_frame` computes, in the same row
    /// bands, each its own submission, with no frame buffer in between.
    fn draw_frame(
        &mut self,
        view: &wgpu::TextureView,
        format: wgpu::TextureFormat,
        encode_srgb: bool,
        width: u32,
        height: u32,
    ) -> Result<(), EngineError> {
        if width == 0 || height == 0 {
            return Err(EngineError::new("zero output size"));
        }
        self.ensure_render_target(width, height)?;
        let pipeline = self.present_pipeline(format);
        if let Some(t) = self.timers.as_mut() {
            t.collect();
        }
        let timer = self.render_timer();
        let l = self.loaded()?;
        let target = self.render_target()?;
        let uniform = self.render_uniform(l, width, height, 0, encode_srgb);
        self.write_buffer(&target.uniform, 0, bytemuck::bytes_of(&uniform));
        let band_rows = render_band_rows(width, height);
        // The simulation batch goes out with the first band.
        let mut batch = self.take_pending();
        let mut y_offset = 0;
        while y_offset < height {
            let rows = band_rows.min(height - y_offset);
            let (mut enc, tick_sample) = match batch.take() {
                Some(p) => (p.enc, p.tick_sample),
                None => (
                    self.ctx
                        .device()
                        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                            label: Some("present"),
                        }),
                    false,
                ),
            };
            if y_offset == 0 {
                self.encode_presence(&mut enc, l, target, timer);
            }
            let last = y_offset + rows >= height;
            {
                let mut pass = enc.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("present"),
                    timestamp_writes: timer.filter(|_| last).map(|t| t.render_writes(false, true)),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view,
                        depth_slice: None,
                        resolve_target: None,
                        ops: wgpu::Operations {
                            load: if y_offset == 0 {
                                wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT)
                            } else {
                                wgpu::LoadOp::Load
                            },
                            store: wgpu::StoreOp::Store,
                        },
                    })],
                    ..Default::default()
                });
                pass.set_pipeline(&pipeline);
                pass.set_bind_group(0, &target.present_bind_group, &[]);
                pass.set_scissor_rect(0, y_offset, width, rows);
                pass.draw(0..3, 0..1);
            }
            CommandCounter::add(&self.counter.passes, 1);
            if let Some(t) = timer.filter(|_| last) {
                t.resolve(&mut enc);
            }
            self.submit_batch(enc, tick_sample)?;
            y_offset += rows;
        }
        if let Some(t) = timer {
            t.request();
        }
        Ok(())
    }

    /// The render timer when it is free to take a sample.
    fn render_timer(&self) -> Option<&GpuTimer> {
        self.timers.as_ref().map(|t| &t.render).filter(|t| t.idle())
    }

    /// The optics pass alone, with no readback or presentation; for timing.
    pub fn render_without_readback(&mut self, width: u32, height: u32) -> Result<(), EngineError> {
        self.render_frame(width, height)
    }

    /// Draws the frame as `present` does into an offscreen `rgba8unorm`
    /// target, a browser canvas's format, and returns its RGBA bytes row by
    /// row; with `read_back` false it only draws, for timing.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn present_offscreen(
        &mut self,
        width: u32,
        height: u32,
        read_back: bool,
    ) -> Result<Option<Vec<u8>>, EngineError> {
        let format = wgpu::TextureFormat::Rgba8Unorm;
        let size = wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        };
        let texture = self.ctx.device().create_texture(&wgpu::TextureDescriptor {
            label: Some("offscreen-present"),
            size,
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        self.draw_frame(&view, format, true, width, height)?;
        if !read_back {
            return Ok(None);
        }
        let row = (width * 4).next_multiple_of(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT);
        let staging = self.buffer(
            "offscreen-readback",
            u64::from(row) * u64::from(height),
            wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        )?;
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("offscreen-readback"),
            });
        enc.copy_texture_to_buffer(
            texture.as_image_copy(),
            wgpu::TexelCopyBufferInfo {
                buffer: &staging,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(row),
                    rows_per_image: Some(height),
                },
            },
            size,
        );
        self.submit(enc.finish())?;
        let data = self.map_read(&staging)?;
        let mut rgba = Vec::with_capacity((width * height * 4) as usize);
        for line in data.chunks(row as usize).take(height as usize) {
            rgba.extend_from_slice(&line[..(width * 4) as usize]);
        }
        Ok(Some(rgba))
    }

    fn copy_frame_to_staging(&self, width: u32, height: u32) -> Result<&wgpu::Buffer, EngineError> {
        let readback = self
            .render_target()?
            .readback
            .as_ref()
            .ok_or_else(|| EngineError::new("no readback buffer"))?;
        let bytes = (width as u64) * (height as u64) * 16;
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("readback"),
            });
        enc.copy_buffer_to_buffer(&readback.out, 0, &readback.staging, 0, bytes);
        CommandCounter::add(&self.counter.copies, 1);
        self.submit(enc.finish())?;
        Ok(&readback.staging)
    }

    /// `Renderer::render` for hosts that cannot block on a buffer map.
    pub async fn render_async(&mut self, width: u32, height: u32) -> Result<Image, EngineError> {
        self.render_frame(width, height)?;
        let staging = self.copy_frame_to_staging(width, height)?;
        let data = self.map_read_async(staging).await?;
        let floats: &[f32] = bytemuck::cast_slice(&data);
        Ok(Image {
            width,
            height,
            rgba: floats.to_vec(),
        })
    }

    /// Renders the current state at the surface's size and presents it.
    /// `Ok(false)` means the swapchain had no texture this frame (occluded,
    /// resized underneath us); the caller simply tries again next frame.
    pub fn present(&mut self, surface: &PresentSurface) -> Result<bool, EngineError> {
        self.ctx.check()?;
        let (width, height) = surface.size();
        let Some(frame) = surface.acquire(&self.ctx)? else {
            return Ok(false);
        };
        let view = frame
            .texture
            .create_view(&wgpu::TextureViewDescriptor::default());
        self.draw_frame(
            &view,
            surface.format(),
            surface.encodes_srgb_in_shader(),
            width,
            height,
        )?;
        self.ctx.queue().present(frame);
        Ok(true)
    }

    fn present_pipeline(&self, format: wgpu::TextureFormat) -> wgpu::RenderPipeline {
        let mut cache = self
            .present
            .cached
            .lock()
            .unwrap_or_else(|p| p.into_inner());
        if let Some((_, pipeline)) = cache.iter().find(|(f, _)| *f == format) {
            return pipeline.clone();
        }
        let pipeline = self.build_present_pipeline(format);
        cache.push((format, pipeline.clone()));
        pipeline
    }

    fn build_present_pipeline(&self, format: wgpu::TextureFormat) -> wgpu::RenderPipeline {
        self.ctx
            .device()
            .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("watercolour-present"),
                layout: Some(&self.present.pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &self.present.module,
                    entry_point: Some("vs_fullscreen"),
                    compilation_options: Default::default(),
                    buffers: &[],
                },
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                fragment: Some(wgpu::FragmentState {
                    module: &self.present.module,
                    entry_point: Some("fs_render"),
                    compilation_options: Default::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend: None,
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            })
    }

    /// Zero when the budget is below one checkpoint: a playback then holds
    /// none, not even tick 0's, and a backwards seek reloads the scene.
    fn checkpoint_capacity_for(&self, layout: &StateLayout) -> usize {
        ((self.checkpoint_budget / layout.state_bytes().max(1)) as usize).min(MAX_CHECKPOINTS)
    }
}

#[derive(Default)]
struct MapState {
    result: Option<Result<(), String>>,
    waker: Option<Waker>,
}

struct MapFuture {
    shared: Arc<Mutex<MapState>>,
}

impl Future for MapFuture {
    type Output = Result<(), String>;

    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let mut st = self.shared.lock().unwrap_or_else(|p| p.into_inner());
        match st.result.take() {
            Some(r) => Poll::Ready(r),
            None => {
                st.waker = Some(cx.waker().clone());
                Poll::Pending
            }
        }
    }
}

impl Simulator for GpuEngine {
    fn load(&mut self, scene: &Scene) -> Result<(), EngineError> {
        scene
            .validate()
            .map_err(|e| EngineError::new(format!("invalid scene: {e:?}")))?;
        let res = scene.sim_resolution.0;
        let pigment_count = scene.palette.len();
        let paper_sim = PaperField::generate_with_aspect(&scene.paper, res, res, scene.aspect());
        let grid = SimulationGrid::new(&paper_sim, pigment_count)
            .with_composite_mode(scene.composite_mode())
            .with_swirl_seed(scene.seed);
        let layout = StateLayout::new(res, res, pigment_count);
        let f = std::mem::size_of::<f32>() as u64;

        drop(self.take_pending());
        self.loaded = None;
        let state = self.buffer(
            "state",
            layout.state_bytes(),
            wgpu::BufferUsages::STORAGE
                | wgpu::BufferUsages::COPY_SRC
                | wgpu::BufferUsages::COPY_DST,
        )?;
        let scratch = self.buffer(
            "scratch",
            layout.scratch_len() as u64 * f,
            wgpu::BufferUsages::STORAGE
                | wgpu::BufferUsages::COPY_SRC
                | wgpu::BufferUsages::COPY_DST,
        )?;
        let stamp = self.buffer(
            "stamp",
            layout.n as u64 * f,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        )?;
        let params = self.buffer(
            "params",
            std::mem::size_of::<ParamsUniform>() as u64,
            wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        )?;
        let stroke = self.buffer(
            "stroke",
            u64::from(self.stroke_offset(STROKE_SLOTS)),
            wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        )?;
        let pigments = self.buffer(
            "pigments",
            (MAX_PIGMENTS * std::mem::size_of::<PigmentCoefUniform>()) as u64,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        )?;
        let optics = self.buffer(
            "optics",
            (MAX_PIGMENTS * 3 * 16) as u64,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        )?;

        self.write_into_zeroed(&state, &layout.pack(&grid));
        self.write_buffer(
            &params,
            0,
            bytemuck::bytes_of(&ParamsUniform::new(
                res,
                res,
                pigment_count as u32,
                grid.aspect,
                &self.params,
            )),
        );
        let coefs: Vec<PigmentCoefUniform> = PigmentCoefficients::from_palette(&scene.palette)
            .iter()
            .map(|c| PigmentCoefUniform {
                density: c.density,
                staining_power: c.staining_power,
                granulation: c.granulation,
                _pad: 0.0,
            })
            .collect();
        self.write_buffer(&pigments, 0, bytemuck::cast_slice(&coefs));
        let mut optics_data: Vec<[f32; 4]> = Vec::with_capacity(pigment_count * 3);
        for p in scene.palette.pigments() {
            optics_data.push([p.k.0[0], p.k.0[1], p.k.0[2], 0.0]);
            optics_data.push([p.s.0[0], p.s.0[1], p.s.0[2], 0.0]);
            optics_data.push([p.granulation, 0.0, 0.0, 0.0]);
        }
        self.write_buffer(&optics, 0, bytemuck::cast_slice(&optics_data));

        let bind_group =
            self.ctx
                .device()
                .create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("watercolour-sim"),
                    layout: &self.sim.layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: params.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: state.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: scratch.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 3,
                            resource: stamp.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 4,
                            resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                                buffer: &stroke,
                                offset: 0,
                                size: wgpu::BufferSize::new(
                                    std::mem::size_of::<StrokeUniform>() as u64
                                ),
                            }),
                        },
                        wgpu::BindGroupEntry {
                            binding: 5,
                            resource: pigments.as_entire_binding(),
                        },
                    ],
                });

        self.loaded = Some(Loaded {
            width: res,
            height: res,
            layout,
            paper: scene.paper,
            paper_sim,
            state,
            stamp,
            stroke,
            optics,
            bind_group,
            checkpoints: HashMap::new(),
            render_cache: None,
            swirl_substeps: swirl::Geometry::new(
                res,
                grid.aspect,
                self.params.swirl_speed,
                self.params.swirl_frequency,
            )
            .substeps,
            maybe_wet: false,
        });
        Ok(())
    }

    fn apply(&mut self, op: &Operation, seed: Seed) -> Result<(), EngineError> {
        let stamp_params: StampParams = self.params.stamp;
        match op {
            Operation::Brush(_) | Operation::Water(_) | Operation::Lift(_) => {
                self.loaded_mut()?.maybe_wet = true;
            }
            Operation::DryAll => self.loaded_mut()?.maybe_wet = false,
            Operation::Dry { .. }
            | Operation::Settle { .. }
            | Operation::SetMask(_)
            | Operation::ClearMask => {}
        }
        let l = self.loaded()?;
        let (w, h, aspect) = (l.width, l.height, l.paper_sim.aspect);
        let paper_height = &l.paper_sim.height;
        let rasterize = |path: &[_], radius, softness, span: StrokeSpan| {
            paint::rasterize_path_span(
                path,
                radius,
                softness,
                StampTarget {
                    width: w,
                    height: h,
                    paper_height,
                },
                aspect,
                seed,
                stamp_params,
                span,
            )
        };
        match op {
            Operation::Brush(s) => {
                let stamp = rasterize(&s.path, s.radius, s.softness, s.span);
                let flow = paint::stroke_flow(&s.path, s.span, aspect, s.water, self.params.flow);
                self.apply_stamp(
                    &self.sim.apply_brush,
                    &stamp,
                    StrokeUniform {
                        kind: 0,
                        pigment: s.pigment as u32,
                        kick_x: flow.kick.0,
                        kick_y: flow.kick.1,
                        concentration: s.concentration,
                        water: s.water,
                        strength: 0.0,
                        splat_out: flow.splat_out,
                        ..StrokeUniform::zeroed()
                    },
                )
            }
            Operation::Water(s) => {
                let stamp = rasterize(&s.path, s.radius, s.softness, s.span);
                let flow = paint::stroke_flow(&s.path, s.span, aspect, s.water, self.params.flow);
                self.apply_stamp(
                    &self.sim.apply_water,
                    &stamp,
                    StrokeUniform {
                        kind: 1,
                        pigment: 0,
                        kick_x: flow.kick.0,
                        kick_y: flow.kick.1,
                        concentration: 0.0,
                        water: s.water,
                        strength: 0.0,
                        splat_out: flow.splat_out,
                        ..StrokeUniform::zeroed()
                    },
                )
            }
            Operation::Lift(s) => {
                let stamp = rasterize(&s.path, s.radius, s.softness, s.span);
                self.apply_stamp(
                    &self.sim.apply_lift,
                    &stamp,
                    StrokeUniform {
                        kind: 2,
                        pigment: 0,
                        kick_x: 0.0,
                        kick_y: 0.0,
                        concentration: 0.0,
                        water: 0.0,
                        strength: s.strength,
                        splat_out: 0.0,
                        ..StrokeUniform::zeroed()
                    },
                )
            }
            Operation::Dry { rate } => {
                let off = self.loaded()?.layout.dry_rate();
                self.write_state_region(off, &[rate.clamp(0.0, 64.0)])
            }
            Operation::Settle { share } => {
                let off = self.loaded()?.layout.settle_share();
                self.write_state_region(off, &[share.clamp(0.0, MAX_SETTLE_SHARE)])
            }
            Operation::DryAll => {
                let n = self.loaded()?.layout.n as u32;
                self.dispatch_apply(&self.sim.dry_all, n, 0)
            }
            Operation::SetMask(mask) => {
                let field = paint::rasterize_mask_aspect(mask, w, h, aspect);
                let off = self.loaded()?.layout.m();
                self.write_state_region(off, &field)
            }
            Operation::ClearMask => {
                let (off, n) = {
                    let l = self.loaded()?;
                    (l.layout.m(), l.layout.n)
                };
                self.write_state_region(off, &vec![1.0f32; n])
            }
        }
    }

    fn tick(&mut self) -> Result<(), EngineError> {
        self.step(1)
    }

    fn step(&mut self, ticks: u32) -> Result<(), EngineError> {
        if ticks == 0 {
            return Ok(());
        }
        let sample_free = !self.with_pending(|p| p.tick_sample);
        if let Some(t) = self.timers.as_mut() {
            t.collect();
            if t.tick.idle() && sample_free {
                t.sampled_ticks = ticks;
            }
        }
        let timer = self
            .timers
            .as_ref()
            .map(|t| &t.tick)
            .filter(|t| t.idle() && sample_free);
        let l = self.loaded()?;
        let mut remaining = ticks;
        while remaining > 0 {
            let full = self.with_pending(|p| {
                let batch = remaining.min(TICKS_PER_SUBMIT - p.ticks);
                let (first, last) = (remaining == ticks, remaining == batch);
                {
                    let mut pass = p.enc.begin_compute_pass(&wgpu::ComputePassDescriptor {
                        label: Some("ticks"),
                        timestamp_writes: timer
                            .filter(|_| first || last)
                            .map(|t| t.compute_writes(first, last)),
                    });
                    CommandCounter::add(&self.counter.passes, 1);
                    pass.set_bind_group(0, &l.bind_group, &[0]);
                    for _ in 0..batch {
                        self.encode_tick(&mut pass, l);
                    }
                }
                if let Some(t) = timer.filter(|_| last) {
                    t.resolve(&mut p.enc);
                    p.tick_sample = true;
                }
                p.ticks += batch;
                remaining -= batch;
                p.ticks >= TICKS_PER_SUBMIT
            });
            if full {
                self.flush()?;
            }
        }
        Ok(())
    }

    fn snapshot(&mut self) -> Result<Option<CheckpointId>, EngineError> {
        let capacity = self.checkpoint_capacity();
        let l = self.loaded()?;
        if l.checkpoints.len() >= capacity {
            return Ok(None);
        }
        let bytes = l.layout.state_bytes();
        let copy = self.buffer(
            "checkpoint",
            bytes,
            wgpu::BufferUsages::COPY_SRC | wgpu::BufferUsages::COPY_DST,
        )?;
        self.with_pending(|p| p.enc.copy_buffer_to_buffer(&l.state, 0, &copy, 0, bytes));
        CommandCounter::add(&self.counter.copies, 1);
        let id = CheckpointId(self.next_checkpoint);
        self.next_checkpoint += 1;
        self.loaded_mut()?.checkpoints.insert(id, copy);
        Ok(Some(id))
    }

    fn restore(&mut self, id: CheckpointId) -> Result<(), EngineError> {
        let l = self.loaded()?;
        let src = l
            .checkpoints
            .get(&id)
            .ok_or_else(|| EngineError::new(format!("unknown checkpoint {id:?}")))?;
        self.with_pending(|p| {
            p.enc
                .copy_buffer_to_buffer(src, 0, &l.state, 0, l.layout.state_bytes())
        });
        CommandCounter::add(&self.counter.copies, 1);
        self.loaded_mut()?.maybe_wet = true;
        Ok(())
    }

    fn release(&mut self, id: CheckpointId) {
        if let Some(l) = self.loaded.as_mut() {
            l.checkpoints.remove(&id);
        }
    }

    fn checkpoint_capacity(&self) -> usize {
        match &self.loaded {
            Some(l) => self.checkpoint_capacity_for(&l.layout),
            None => 0,
        }
    }
}

#[cfg(not(target_arch = "wasm32"))]
impl Renderer for GpuEngine {
    fn render(&mut self, width: u32, height: u32) -> Result<Image, EngineError> {
        self.render_frame(width, height)?;
        let staging = self.copy_frame_to_staging(width, height)?;
        let data = self.map_read(staging)?;
        let floats: &[f32] = bytemuck::cast_slice(&data);
        Ok(Image {
            width,
            height,
            rgba: floats.to_vec(),
        })
    }
}

impl std::fmt::Debug for GpuEngine {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GpuEngine")
            .field("adapter", &self.ctx.adapter_name())
            .field("loaded", &self.loaded.is_some())
            .finish()
    }
}
