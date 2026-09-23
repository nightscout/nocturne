use std::collections::{HashMap, VecDeque};
use std::future::Future;
use std::pin::Pin;
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
    _p1: u32,
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
    blur_h: wgpu::ComputePipeline,
    blur_v: wgpu::ComputePipeline,
    advect: wgpu::ComputePipeline,
    swirl_distance_h: wgpu::ComputePipeline,
    swirl_distance_v: wgpu::ComputePipeline,
    swirl_stream: wgpu::ComputePipeline,
    swirl: wgpu::ComputePipeline,
    clock: wgpu::ComputePipeline,
    transfer: wgpu::ComputePipeline,
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
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct PresentUniform {
    width: u32,
    height: u32,
    encode_srgb: u32,
    _pad: u32,
}

/// The swapchain format is only known once a surface exists, so the render
/// pipeline is built on first present and rebuilt if the format changes.
#[derive(Clone)]
struct PresentPipeline {
    layout: wgpu::BindGroupLayout,
    pipeline_layout: wgpu::PipelineLayout,
    module: wgpu::ShaderModule,
    cached: Option<(wgpu::TextureFormat, wgpu::RenderPipeline)>,
}

struct Loaded {
    width: u32,
    height: u32,
    layout: StateLayout,
    paper: Paper,
    paper_sim: PaperField,
    state: wgpu::Buffer,
    scratch: wgpu::Buffer,
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
    out: wgpu::Buffer,
    /// Host-visible copy of `out`, created on the first readback. A
    /// presenting instance never reads back, so it never pays for one.
    staging: Option<wgpu::Buffer>,
    bind_group: wgpu::BindGroup,
    present_uniform: wgpu::Buffer,
    present_bind_group: wgpu::BindGroup,
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
const PRESENT_SOURCE: &str = include_str!("shaders/present.wgsl");

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
                uniform_entry(4),
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
            blur_h: make("blur_h"),
            blur_v: make("blur_v"),
            advect: make("advect"),
            swirl_distance_h: make("swirl_distance_h"),
            swirl_distance_v: make("swirl_distance_v"),
            swirl_stream: make("swirl_stream"),
            swirl: make("swirl"),
            clock: make("clock"),
            transfer: make("transfer"),
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

        let present_module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("watercolour-present"),
            source: wgpu::ShaderSource::Wgsl(PRESENT_SOURCE.into()),
        });
        let present_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("watercolour-present"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Storage { read_only: true },
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
            ],
        });
        let present_pipeline_layout =
            device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("watercolour-present"),
                bind_group_layouts: &[Some(&present_layout)],
                ..Default::default()
            });

        let validation = error_scope.pop();
        let engine = GpuEngine {
            ctx,
            params,
            render_params,
            sim,
            render: RenderPipeline {
                layout: render_layout,
                pipeline: render_pipeline,
            },
            present: PresentPipeline {
                layout: present_layout,
                pipeline_layout: present_pipeline_layout,
                module: present_module,
                cached: None,
            },
            loaded: None,
            next_checkpoint: 1,
            checkpoint_budget: CHECKPOINT_BUDGET_BYTES,
            in_flight: Mutex::new(VecDeque::new()),
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

    pub fn context(&self) -> &GpuContext {
        &self.ctx
    }

    /// Blocks until every submitted command has finished; used for timing.
    pub fn sync(&self) -> Result<(), EngineError> {
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

    /// Encodes one tick: the same pass order as `sim::step`, with scratch
    /// regions copied back into `state` where the CPU swaps vectors.
    fn encode_tick(&self, enc: &mut wgpu::CommandEncoder, l: &Loaded) {
        let n = l.layout.n as u32;
        let f = std::mem::size_of::<f32>() as u64;
        let region = |elems: usize| (elems as u64) * f;
        let copy = |enc: &mut wgpu::CommandEncoder, from: usize, to: usize, elems: usize| {
            enc.copy_buffer_to_buffer(
                &l.scratch,
                region(from),
                &l.state,
                region(to),
                region(elems),
            );
        };
        let dispatch = |enc: &mut wgpu::CommandEncoder, pipeline: &wgpu::ComputePipeline| {
            let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &l.bind_group, &[]);
            pass.dispatch_workgroups(groups(n), 1, 1);
        };
        let lay = &l.layout;

        dispatch(enc, &self.sim.velocity);
        copy(enc, lay.scratch_u(), lay.u(), lay.n);
        copy(enc, lay.scratch_v(), lay.v(), lay.n);

        dispatch(enc, &self.sim.divergence);
        enc.clear_buffer(&l.scratch, region(lay.scratch_q()), Some(region(lay.n)));
        for i in 0..self.params.jacobi_iterations {
            dispatch(
                enc,
                if i % 2 == 0 {
                    &self.sim.jacobi_a
                } else {
                    &self.sim.jacobi_b
                },
            );
        }
        if self.params.jacobi_iterations % 2 == 1 {
            enc.copy_buffer_to_buffer(
                &l.scratch,
                region(lay.scratch_q2()),
                &l.scratch,
                region(lay.scratch_q()),
                region(lay.n),
            );
        }
        dispatch(enc, &self.sim.project);
        copy(enc, lay.scratch_u(), lay.u(), lay.n);
        copy(enc, lay.scratch_v(), lay.v(), lay.n);

        dispatch(enc, &self.sim.blur_h);
        dispatch(enc, &self.sim.blur_v);
        dispatch(enc, &self.sim.advect);
        copy(enc, lay.scratch_g(0), lay.g(0), lay.n * lay.pigment_count);
        copy(enc, lay.scratch_p(), lay.p(), lay.n);

        if self.params.swirl_speed > 0.0 && l.maybe_wet {
            dispatch(enc, &self.sim.swirl_distance_h);
            dispatch(enc, &self.sim.swirl_distance_v);
            let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(&self.sim.swirl_stream);
            pass.set_bind_group(0, &l.bind_group, &[]);
            pass.dispatch_workgroups(groups(lay.corner_count() as u32), 1, 1);
            drop(pass);
            for _ in 0..l.swirl_substeps {
                dispatch(enc, &self.sim.swirl);
                copy(enc, lay.scratch_g(0), lay.g(0), lay.n * lay.pigment_count);
            }
        }

        dispatch(enc, &self.sim.transfer);

        dispatch(enc, &self.sim.capillary);
        dispatch(enc, &self.sim.capillary_wet);
        copy(enc, lay.scratch_s(), lay.s(), lay.n);

        let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
        pass.set_pipeline(&self.sim.clock);
        pass.set_bind_group(0, &l.bind_group, &[]);
        pass.dispatch_workgroups(1, 1, 1);
    }

    fn write_state_region(&self, offset_elems: usize, data: &[f32]) -> Result<(), EngineError> {
        let l = self.loaded()?;
        self.ctx.queue().write_buffer(
            &l.state,
            (offset_elems * std::mem::size_of::<f32>()) as u64,
            bytemuck::cast_slice(data),
        );
        Ok(())
    }

    fn dispatch_apply(&self, pipeline: &wgpu::ComputePipeline) -> Result<(), EngineError> {
        let l = self.loaded()?;
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("apply"),
            });
        {
            let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &l.bind_group, &[]);
            pass.dispatch_workgroups(groups(l.layout.n as u32), 1, 1);
        }
        self.submit(enc.finish())
    }

    fn upload_stamp(&self, stamp: &paint::Stamp, stroke: StrokeUniform) -> Result<(), EngineError> {
        let l = self.loaded()?;
        let q = self.ctx.queue();
        q.write_buffer(&l.stamp, 0, bytemuck::cast_slice(&stamp.coverage));
        q.write_buffer(&l.stroke, 0, bytemuck::bytes_of(&stroke));
        Ok(())
    }

    /// Reads the whole state back; used by tests and the CPU comparison.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn read_grid(&self) -> Result<SimulationGrid, EngineError> {
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
        let paper_out =
            PaperField::generate_with_pixel_scale(&paper, width, height, aspect, pixel_scale);
        let pixels = (width as u64) * (height as u64);
        let uniform = self.buffer(
            "render-params",
            std::mem::size_of::<RenderUniform>() as u64,
            wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        )?;
        let paper_buf = self.buffer(
            "paper-out",
            pixels * 4,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        )?;
        let out = self.buffer(
            "render-out",
            pixels * 16,
            wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
        )?;
        self.ctx
            .queue()
            .write_buffer(&paper_buf, 0, bytemuck::cast_slice(&paper_out.height));
        let l = self.loaded()?;
        let bind_group = self
            .ctx
            .device()
            .create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("watercolour-render"),
                layout: &self.render.layout,
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
                        binding: 4,
                        resource: out.as_entire_binding(),
                    },
                ],
            });
        let present_uniform = self.buffer(
            "present-params",
            std::mem::size_of::<PresentUniform>() as u64,
            wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        )?;
        let present_bind_group = self
            .ctx
            .device()
            .create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("watercolour-present"),
                layout: &self.present.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: present_uniform.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: out.as_entire_binding(),
                    },
                ],
            });
        self.loaded_mut()?.render_cache = Some(RenderTarget {
            width,
            height,
            pixel_scale,
            uniform,
            out,
            staging: None,
            bind_group,
            present_uniform,
            present_bind_group,
        });
        Ok(())
    }

    /// Runs the optics pass into the cached output buffer at `width` x
    /// `height`; readback and presentation both start from here. The frame
    /// is rendered in row bands (see [`RENDER_PIXELS_PER_DISPATCH`]); each
    /// band's uniform write is queued ahead of its own submission, so the
    /// bands execute in order against the same uniform buffer.
    fn render_frame(&mut self, width: u32, height: u32) -> Result<(), EngineError> {
        if width == 0 || height == 0 {
            return Err(EngineError::new("zero output size"));
        }
        self.ensure_render_target(width, height)?;
        let l = self.loaded()?;
        let target = self.render_target()?;
        let band_rows = render_band_rows(width, height);
        let mut y_offset = 0;
        while y_offset < height {
            let rows = band_rows.min(height - y_offset);
            let uniform = RenderUniform {
                out_width: width,
                out_height: height,
                sim_width: l.width,
                sim_height: l.height,
                pigment_count: l.layout.pigment_count as u32,
                n: l.layout.n as u32,
                y_offset,
                _p1: 0,
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
            };
            self.ctx
                .queue()
                .write_buffer(&target.uniform, 0, bytemuck::bytes_of(&uniform));
            let mut enc =
                self.ctx
                    .device()
                    .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                        label: Some("render"),
                    });
            {
                let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
                pass.set_pipeline(&self.render.pipeline);
                pass.set_bind_group(0, &target.bind_group, &[]);
                pass.dispatch_workgroups(width.div_ceil(16), rows.div_ceil(16), 1);
            }
            self.submit(enc.finish())?;
            y_offset += rows;
        }
        Ok(())
    }

    fn ensure_staging(&mut self) -> Result<(), EngineError> {
        let target = self.render_target()?;
        if target.staging.is_some() {
            return Ok(());
        }
        let bytes = (target.width as u64) * (target.height as u64) * 16;
        let staging = self.buffer(
            "render-staging",
            bytes,
            wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        )?;
        let target = self
            .loaded_mut()?
            .render_cache
            .as_mut()
            .ok_or_else(|| EngineError::new("no rendered frame"))?;
        target.staging = Some(staging);
        Ok(())
    }

    fn copy_frame_to_staging(&self, width: u32, height: u32) -> Result<&wgpu::Buffer, EngineError> {
        let target = self.render_target()?;
        let staging = target
            .staging
            .as_ref()
            .ok_or_else(|| EngineError::new("no staging buffer"))?;
        let bytes = (width as u64) * (height as u64) * 16;
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("readback"),
            });
        enc.copy_buffer_to_buffer(&target.out, 0, staging, 0, bytes);
        self.submit(enc.finish())?;
        Ok(staging)
    }

    /// `Renderer::render` for hosts that cannot block on a buffer map.
    pub async fn render_async(&mut self, width: u32, height: u32) -> Result<Image, EngineError> {
        self.render_frame(width, height)?;
        self.ensure_staging()?;
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
        self.render_frame(width, height)?;
        let format = surface.format();
        if !matches!(&self.present.cached, Some((f, _)) if *f == format) {
            let pipeline = self.build_present_pipeline(format);
            self.present.cached = Some((format, pipeline));
        }
        let Some(frame) = surface.acquire(&self.ctx)? else {
            return Ok(false);
        };
        let target = self.render_target()?;
        self.ctx.queue().write_buffer(
            &target.present_uniform,
            0,
            bytemuck::bytes_of(&PresentUniform {
                width,
                height,
                encode_srgb: u32::from(surface.encodes_srgb_in_shader()),
                _pad: 0,
            }),
        );
        let view = frame
            .texture
            .create_view(&wgpu::TextureViewDescriptor::default());
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("present"),
            });
        {
            let (_, pipeline) = self
                .present
                .cached
                .as_ref()
                .ok_or_else(|| EngineError::new("no present pipeline"))?;
            let mut pass = enc.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("present"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &target.present_bind_group, &[]);
            pass.draw(0..3, 0..1);
        }
        self.submit(enc.finish())?;
        self.ctx.queue().present(frame);
        Ok(true)
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
                    entry_point: Some("fs_present"),
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

    fn checkpoint_capacity_for(&self, layout: &StateLayout) -> usize {
        ((self.checkpoint_budget / layout.state_bytes().max(1)) as usize).clamp(1, MAX_CHECKPOINTS)
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
            std::mem::size_of::<StrokeUniform>() as u64,
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

        let q = self.ctx.queue();
        q.write_buffer(&state, 0, bytemuck::cast_slice(&layout.pack(&grid)));
        q.write_buffer(
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
        q.write_buffer(&pigments, 0, bytemuck::cast_slice(&coefs));
        let mut optics_data: Vec<[f32; 4]> = Vec::with_capacity(pigment_count * 3);
        for p in scene.palette.pigments() {
            optics_data.push([p.k.0[0], p.k.0[1], p.k.0[2], 0.0]);
            optics_data.push([p.s.0[0], p.s.0[1], p.s.0[2], 0.0]);
            optics_data.push([p.granulation, 0.0, 0.0, 0.0]);
        }
        q.write_buffer(&optics, 0, bytemuck::cast_slice(&optics_data));

        let bind_group = self
            .ctx
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
                        resource: stroke.as_entire_binding(),
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
            scratch,
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
        let (w, h, aspect, paper_height) = {
            let l = self.loaded()?;
            (
                l.width,
                l.height,
                l.paper_sim.aspect,
                l.paper_sim.height.clone(),
            )
        };
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
        let rasterize = |path: &[_], radius, softness, span: StrokeSpan| {
            paint::rasterize_path_span(
                path,
                radius,
                softness,
                StampTarget {
                    width: w,
                    height: h,
                    paper_height: &paper_height,
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
                self.upload_stamp(
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
                    },
                )?;
                self.dispatch_apply(&self.sim.apply_brush)
            }
            Operation::Water(s) => {
                let stamp = rasterize(&s.path, s.radius, s.softness, s.span);
                let flow = paint::stroke_flow(&s.path, s.span, aspect, s.water, self.params.flow);
                self.upload_stamp(
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
                    },
                )?;
                self.dispatch_apply(&self.sim.apply_water)
            }
            Operation::Lift(s) => {
                let stamp = rasterize(&s.path, s.radius, s.softness, s.span);
                self.upload_stamp(
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
                    },
                )?;
                self.dispatch_apply(&self.sim.apply_lift)
            }
            Operation::Dry { rate } => {
                let off = self.loaded()?.layout.dry_rate();
                self.write_state_region(off, &[rate.clamp(0.0, 64.0)])
            }
            Operation::Settle { share } => {
                let off = self.loaded()?.layout.settle_share();
                self.write_state_region(off, &[share.clamp(0.0, MAX_SETTLE_SHARE)])
            }
            Operation::DryAll => self.dispatch_apply(&self.sim.dry_all),
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
        let l = self.loaded()?;
        let mut remaining = ticks;
        while remaining > 0 {
            let batch = remaining.min(TICKS_PER_SUBMIT);
            let mut enc =
                self.ctx
                    .device()
                    .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                        label: Some("ticks"),
                    });
            for _ in 0..batch {
                self.encode_tick(&mut enc, l);
            }
            self.submit(enc.finish())?;
            remaining -= batch;
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
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("snapshot"),
            });
        enc.copy_buffer_to_buffer(&l.state, 0, &copy, 0, bytes);
        self.submit(enc.finish())?;
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
        let mut enc = self
            .ctx
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("restore"),
            });
        enc.copy_buffer_to_buffer(src, 0, &l.state, 0, l.layout.state_bytes());
        self.submit(enc.finish())?;
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
        self.ensure_staging()?;
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
