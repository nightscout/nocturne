//! wasm-bindgen surface. One `WatercolourEngine` per page owns the WebGPU
//! device and the compiled pipelines; each `SceneInstance` forks those
//! pipelines for its own scene and canvas. Every error crosses to JS as an
//! `Error` whose message is `Code: detail` (see `scene_tools::SceneToolError`
//! for the scene codes; the engine adds `WebGpuUnavailable`, `InitFailed`,
//! `InstanceLimit`, `DeviceLost`, `NoSurface` and `Engine`).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::rc::Rc;

use nocturne_watercolour_core::application::playback::DEFAULT_PAINT_WALL_FRACTION;
use nocturne_watercolour_core::application::{
    EngineError, Exporter, Playback, PlaybackState, ProgressCurve,
};
use nocturne_watercolour_core::domain::{Image, Scene, Seed};
use nocturne_watercolour_infra::document::{parse_appended_json, parse_scene_json};
use nocturne_watercolour_infra::export::PngExporter;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine, PresentSurface};
use wasm_bindgen::prelude::*;

use crate::scene_tools::{self, BakedManifest, Surface};
use nocturne_watercolour_infra::authoring::DetailLevel;

const DEFAULT_MAX_LIVE_INSTANCES: u32 = 4;

/// Per-instance checkpoint budget in the browser. Several instances share
/// one device and browsers cap GPU memory per tab, so this is a fraction of
/// the native budget: ten checkpoints at the catalogue's 256^2 x 4 pigments.
const BROWSER_CHECKPOINT_BUDGET_BYTES: u64 = 48 * 1024 * 1024;

#[wasm_bindgen(typescript_custom_section)]
const TS_TYPES: &str = r#"
export interface EngineStats {
  liveInstances: number;
  maxLiveInstances: number;
  checkpointBytes: number;
  lastStepMs: number;
  lastRenderMs: number;
  /** GPU milliseconds per tick from timestamp queries; null without the
   * `timestamp-query` feature or before the first sample is read back. */
  gpuTickMs: number | null;
  /** GPU milliseconds of the latest sampled frame's optics. */
  gpuRenderMs: number | null;
  initMs: number;
  adapterName: string;
}
"#;

thread_local! {
    /// Keyed by engine: a host that rebuilds its engine after a loss must not
    /// hear the dead device again, nor an old engine's listeners fire for the
    /// new device. A thread-local because the device-lost hook must be `Send`.
    static LOST_CALLBACKS: RefCell<HashMap<u32, Vec<js_sys::Function>>> = RefCell::new(HashMap::new());
    static NEXT_ENGINE_ID: Cell<u32> = const { Cell::new(0) };
}

fn now_ms() -> f64 {
    web_sys::window()
        .and_then(|w| w.performance())
        .map(|p| p.now())
        .unwrap_or_else(js_sys::Date::now)
}

fn js_err(code: &str, detail: impl std::fmt::Display) -> JsError {
    JsError::new(&format!("{code}: {detail}"))
}

fn engine_err(e: EngineError) -> JsError {
    js_err("Engine", e)
}

/// A lost device crosses as `DeviceLost`, which the host answers by tearing
/// the engine down; a device that reported an error (validation, out of
/// memory) crosses as `Engine`, so the instance that hit it falls back while
/// the host keeps its device.
fn guard_device(ctx: &GpuContext) -> Result<(), JsError> {
    if ctx.is_lost() {
        return Err(js_err("DeviceLost", "the GPU device was lost"));
    }
    if let Some(fault) = ctx.fault() {
        return Err(js_err("Engine", format!("device error: {fault}")));
    }
    Ok(())
}

#[derive(Default)]
struct Shared {
    live: Cell<u32>,
    max_live: Cell<u32>,
    checkpoint_bytes: Cell<f64>,
    last_step_ms: Cell<f64>,
    last_render_ms: Cell<f64>,
    gpu_tick_ms: Cell<Option<f64>>,
    gpu_render_ms: Cell<Option<f64>>,
    init_ms: Cell<f64>,
}

#[wasm_bindgen]
pub struct WatercolourEngine {
    ctx: GpuContext,
    template: GpuEngine,
    shared: Rc<Shared>,
    id: u32,
}

impl Drop for WatercolourEngine {
    fn drop(&mut self) {
        let id = self.id;
        LOST_CALLBACKS.with(|cbs| cbs.borrow_mut().remove(&id));
    }
}

#[wasm_bindgen]
impl WatercolourEngine {
    /// Requests the adapter and device and compiles every pipeline once.
    /// Rejects with `WebGpuUnavailable` when the browser offers no adapter.
    pub async fn create() -> Result<WatercolourEngine, JsError> {
        console_error_panic_hook::set_once();
        let t0 = now_ms();
        let ctx = GpuContext::new_browser()
            .await
            .map_err(|e| js_err("InitFailed", e))?
            .ok_or_else(|| js_err("WebGpuUnavailable", "no WebGPU adapter"))?;
        let template = GpuEngine::new_async(ctx.clone())
            .await
            .map_err(|e| js_err("InitFailed", e))?
            .with_checkpoint_budget(BROWSER_CHECKPOINT_BUDGET_BYTES);
        let id = NEXT_ENGINE_ID.with(|next| {
            let id = next.get();
            next.set(id.wrapping_add(1));
            id
        });
        ctx.on_device_lost(move |message| {
            LOST_CALLBACKS.with(|cbs| {
                for cb in cbs.borrow().get(&id).into_iter().flatten() {
                    let _ = cb.call1(&JsValue::NULL, &JsValue::from_str(&message));
                }
            });
        });
        let shared = Rc::new(Shared::default());
        shared.max_live.set(DEFAULT_MAX_LIVE_INSTANCES);
        shared.init_ms.set(now_ms() - t0);
        Ok(WatercolourEngine {
            ctx,
            template,
            shared,
            id,
        })
    }

    /// Parses a versioned scene document and builds a paused playback for
    /// it. Throws `InstanceLimit` once `maxLiveInstances` are alive.
    /// `settle_fraction` (0 = unchanged) lengthens the reveal's drying tail:
    /// the last fraction of the ticks then run at the raised evaporation
    /// rate (`scene_tools::apply_settle_fraction`). `paint_wall_fraction`
    /// (0 = keep the default) is the share of the wall clock the brushwork
    /// gets: the playback's curve becomes `ProgressCurve::reveal_for(scene,
    /// paint_wall_fraction)`, so the tail covers the settling after the pen
    /// leaves the paper. `checkpoint_budget_bytes` (absent or 0 = the
    /// engine's [`BROWSER_CHECKPOINT_BUDGET_BYTES`]) is this instance's own
    /// seek-checkpoint budget; below one checkpoint it keeps none, so `seek`
    /// falls back to reloading the scene and replaying from the start.
    #[wasm_bindgen(js_name = createInstance)]
    pub fn create_instance(
        &self,
        scene_json: &str,
        duration_ms: f64,
        settle_fraction: f64,
        paint_wall_fraction: f64,
        checkpoint_budget_bytes: Option<f64>,
    ) -> Result<SceneInstance, JsError> {
        self.admit()?;
        let scene = parse_scene_json(scene_json).map_err(|e| js_err("InvalidScene", e))?;
        self.instance(
            scene,
            duration_ms,
            settle_fraction,
            paint_wall_fraction,
            checkpoint_budget_bytes,
        )
    }

    /// `createInstance` for a catalogue artwork: `catalogueScene`'s
    /// arguments, then `createInstance`'s after the document. The scene is
    /// authored straight into the playback and never crosses as JSON.
    #[allow(clippy::too_many_arguments)]
    #[wasm_bindgen(js_name = createCatalogueInstance)]
    pub fn create_catalogue_instance(
        &self,
        artwork_id: &str,
        seed: f64,
        palette: &str,
        intensity: f32,
        detail: &str,
        surface: &str,
        sim_resolution: f64,
        duration_ms: f64,
        settle_fraction: f64,
        paint_wall_fraction: f64,
        checkpoint_budget_bytes: Option<f64>,
    ) -> Result<SceneInstance, JsError> {
        self.admit()?;
        let scene = catalogue_scene_value(
            artwork_id,
            seed,
            palette,
            intensity,
            detail,
            surface,
            sim_resolution,
        )?;
        self.instance(
            scene,
            duration_ms,
            settle_fraction,
            paint_wall_fraction,
            checkpoint_budget_bytes,
        )
    }

    /// `createInstance` for a Lucide icon: `iconScene`'s arguments, then
    /// `createInstance`'s after the document, with no JSON in between.
    #[allow(clippy::too_many_arguments)]
    #[wasm_bindgen(js_name = createIconInstance)]
    pub fn create_icon_instance(
        &self,
        elements_json: &str,
        name: &str,
        seed: f64,
        palette: &str,
        intensity: f32,
        detail: &str,
        surface: &str,
        sim_resolution: f64,
        hints_json: &str,
        duration_ms: f64,
        settle_fraction: f64,
        paint_wall_fraction: f64,
        checkpoint_budget_bytes: Option<f64>,
    ) -> Result<SceneInstance, JsError> {
        self.admit()?;
        let scene = icon_scene_value(
            elements_json,
            name,
            seed,
            palette,
            intensity,
            detail,
            surface,
            sim_resolution,
            hints_json,
        )?;
        self.instance(
            scene,
            duration_ms,
            settle_fraction,
            paint_wall_fraction,
            checkpoint_budget_bytes,
        )
    }

    /// Refuses a new instance on a lost or faulted device or past the cap,
    /// before any scene is authored or parsed for it.
    fn admit(&self) -> Result<(), JsError> {
        guard_device(&self.ctx)?;
        if self.shared.live.get() >= self.shared.max_live.get() {
            return Err(js_err(
                "InstanceLimit",
                format!(
                    "{} live instances already (maxLiveInstances)",
                    self.shared.live.get()
                ),
            ));
        }
        Ok(())
    }

    fn instance(
        &self,
        mut scene: Scene,
        duration_ms: f64,
        settle_fraction: f64,
        paint_wall_fraction: f64,
        checkpoint_budget_bytes: Option<f64>,
    ) -> Result<SceneInstance, JsError> {
        if settle_fraction.is_finite() && settle_fraction > 0.0 {
            scene_tools::apply_settle_fraction(&mut scene, settle_fraction as f32);
        }
        let mut engine = self.template.fork();
        if let Some(bytes) = checkpoint_budget_bytes.filter(|b| b.is_finite() && *b > 0.0) {
            engine = engine.with_checkpoint_budget(bytes as u64);
        }
        let mut playback = Playback::new(engine, scene, duration_ms as f32)
            .map_err(|e| js_err("InvalidScene", e))?;
        if paint_wall_fraction.is_finite() && paint_wall_fraction > 0.0 {
            playback.set_progress_curve(ProgressCurve::reveal_for(
                playback.scene(),
                paint_wall_fraction as f32,
            ));
        }
        self.shared.live.set(self.shared.live.get() + 1);
        let mut instance = SceneInstance {
            blend_ticks: false,
            playback,
            surface: None,
            ctx: self.ctx.clone(),
            shared: Rc::clone(&self.shared),
            reported_checkpoint_bytes: 0.0,
        };
        instance.sync_checkpoint_bytes();
        Ok(instance)
    }

    #[wasm_bindgen(js_name = isLost)]
    pub fn is_lost(&self) -> bool {
        self.ctx.is_lost()
    }

    /// `callback(message)` runs when the browser reports the device lost.
    #[wasm_bindgen(js_name = onDeviceLost)]
    pub fn on_device_lost(&self, callback: js_sys::Function) {
        LOST_CALLBACKS.with(|cbs| cbs.borrow_mut().entry(self.id).or_default().push(callback));
    }

    #[wasm_bindgen(getter, js_name = maxLiveInstances)]
    pub fn max_live_instances(&self) -> u32 {
        self.shared.max_live.get()
    }

    #[wasm_bindgen(setter, js_name = maxLiveInstances)]
    pub fn set_max_live_instances(&self, value: u32) {
        self.shared.max_live.set(value.max(1));
    }

    #[wasm_bindgen(js_name = adapterName)]
    pub fn adapter_name(&self) -> String {
        self.ctx.adapter_name().to_string()
    }

    /// `lastStepMs` and `lastRenderMs` are CPU-side (submission, not GPU
    /// completion) in `performance.now()` milliseconds; `gpuTickMs` and
    /// `gpuRenderMs` are GPU timestamps, quantised by the browser (100 us in
    /// Chrome unless it runs with `--enable-dawn-features=allow_unsafe_apis`).
    #[wasm_bindgen(unchecked_return_type = "EngineStats")]
    pub fn stats(&self) -> Result<JsValue, JsError> {
        let obj = js_sys::Object::new();
        let set = |key: &str, value: JsValue| {
            js_sys::Reflect::set(&obj, &JsValue::from_str(key), &value)
                .map(|_| ())
                .map_err(|_| js_err("Engine", "stats object"))
        };
        set("liveInstances", self.shared.live.get().into())?;
        set("maxLiveInstances", self.shared.max_live.get().into())?;
        set("checkpointBytes", self.shared.checkpoint_bytes.get().into())?;
        set("lastStepMs", self.shared.last_step_ms.get().into())?;
        set("lastRenderMs", self.shared.last_render_ms.get().into())?;
        let or_null = |v: Option<f64>| v.map_or(JsValue::NULL, JsValue::from);
        set("gpuTickMs", or_null(self.shared.gpu_tick_ms.get()))?;
        set("gpuRenderMs", or_null(self.shared.gpu_render_ms.get()))?;
        set("initMs", self.shared.init_ms.get().into())?;
        set("adapterName", JsValue::from_str(self.ctx.adapter_name()))?;
        Ok(obj.into())
    }
}

/// Scene JSON for a catalogue artwork. `palette` is a built-in name or a
/// palette document; `intensity` scales pigment concentration (unity at
/// 0.7); `detail` is `small`, `medium`, `large` or `extralarge` for the size
/// the artwork will be shown at; `surface` is `light` or `dark` for the page
/// background (dark selects the luminous compositing mode, not another
/// palette); `simResolution` overrides the simulation grid side for the
/// detail (0 keeps the detail's default, anything else is clamped to
/// `SimResolution::MIN..=MAX`). The TypeScript side passes the DPR-scaled
/// backing long edge rounded up to a multiple of 32.
#[wasm_bindgen(js_name = catalogueScene)]
pub fn catalogue_scene(
    artwork_id: &str,
    seed: f64,
    palette: &str,
    intensity: f32,
    detail: &str,
    surface: &str,
    sim_resolution: f64,
) -> Result<String, JsError> {
    scene_tools::catalogue_scene_json_with_resolution(
        artwork_id,
        seed_arg(seed),
        palette,
        intensity,
        detail_arg(detail)?,
        surface_arg(surface)?,
        sim_arg(sim_resolution),
    )
    .map_err(|e| JsError::new(&e.to_string()))
}

fn seed_arg(seed: f64) -> Seed {
    Seed(if seed.is_finite() {
        seed.max(0.0) as u64
    } else {
        0
    })
}

fn detail_arg(detail: &str) -> Result<DetailLevel, JsError> {
    scene_tools::parse_detail(detail).ok_or_else(|| {
        js_err(
            "InvalidDetail",
            format!("{detail:?} is not small|medium|large|extralarge"),
        )
    })
}

fn surface_arg(surface: &str) -> Result<Surface, JsError> {
    Surface::parse(surface)
        .ok_or_else(|| js_err("InvalidSurface", format!("{surface:?} is not light|dark")))
}

fn sim_arg(sim_resolution: f64) -> Option<u32> {
    (sim_resolution.is_finite() && sim_resolution > 0.0).then_some(sim_resolution as u32)
}

fn catalogue_scene_value(
    artwork_id: &str,
    seed: f64,
    palette: &str,
    intensity: f32,
    detail: &str,
    surface: &str,
    sim_resolution: f64,
) -> Result<Scene, JsError> {
    scene_tools::catalogue_scene_with_resolution(
        artwork_id,
        seed_arg(seed),
        palette,
        intensity,
        detail_arg(detail)?,
        surface_arg(surface)?,
        sim_arg(sim_resolution),
    )
    .map_err(|e| JsError::new(&e.to_string()))
}

#[allow(clippy::too_many_arguments)]
fn icon_scene_value(
    elements_json: &str,
    name: &str,
    seed: f64,
    palette: &str,
    intensity: f32,
    detail: &str,
    surface: &str,
    sim_resolution: f64,
    hints_json: &str,
) -> Result<Scene, JsError> {
    scene_tools::icon_scene(
        elements_json,
        name,
        seed_arg(seed),
        palette,
        intensity,
        detail_arg(detail)?,
        surface_arg(surface)?,
        sim_arg(sim_resolution),
        hints_json,
    )
    .map_err(|e| JsError::new(&e.to_string()))
}

#[wasm_bindgen(js_name = catalogueIds)]
pub fn catalogue_ids() -> Vec<String> {
    scene_tools::catalogue_ids()
}

/// Scene JSON for a Lucide icon. `elements` is the icon element list as JSON
/// (the array a `lucide` `IconNode` serialises to); `name` becomes the scene
/// id (`lucide-<name>-<palette>-<seed>`). `hints_json` is the per-icon tuning
/// (`""` keeps the defaults; camelCase fields). The remaining arguments are as
/// [`catalogue_scene`].
#[allow(clippy::too_many_arguments)]
#[wasm_bindgen(js_name = iconScene)]
pub fn icon_scene(
    elements_json: &str,
    name: &str,
    seed: f64,
    palette: &str,
    intensity: f32,
    detail: &str,
    surface: &str,
    sim_resolution: f64,
    hints_json: &str,
) -> Result<String, JsError> {
    scene_tools::icon_scene_json(
        elements_json,
        name,
        seed_arg(seed),
        palette,
        intensity,
        detail_arg(detail)?,
        surface_arg(surface)?,
        sim_arg(sim_resolution),
        hints_json,
    )
    .map_err(|e| JsError::new(&e.to_string()))
}

#[wasm_bindgen(js_name = bakedManifest)]
pub fn baked_manifest(frames: u32, width: u32, height: u32, duration_ms: u32) -> String {
    BakedManifest::vertical(frames, width, height, duration_ms).to_json()
}

#[wasm_bindgen]
pub struct SceneInstance {
    blend_ticks: bool,
    playback: Playback<GpuEngine>,
    surface: Option<PresentSurface>,
    ctx: GpuContext,
    shared: Rc<Shared>,
    reported_checkpoint_bytes: f64,
}

impl SceneInstance {
    fn sync_checkpoint_bytes(&mut self) {
        let now = self.playback.simulator().checkpoint_bytes() as f64;
        let total = self.shared.checkpoint_bytes.get();
        self.shared
            .checkpoint_bytes
            .set((total + now - self.reported_checkpoint_bytes).max(0.0));
        self.reported_checkpoint_bytes = now;
    }

    fn guard_device(&self) -> Result<(), JsError> {
        guard_device(&self.ctx)
    }

    fn sync_gpu_timings(&mut self) {
        let timings = self.playback.simulator().gpu_timings();
        if timings.tick_ms.is_some() {
            self.shared.gpu_tick_ms.set(timings.tick_ms);
        }
        if timings.render_ms.is_some() {
            self.shared.gpu_render_ms.set(timings.render_ms);
        }
    }

    fn timed_step(
        &mut self,
        f: impl FnOnce(&mut Playback<GpuEngine>) -> Result<(), EngineError>,
    ) -> Result<(), JsError> {
        self.guard_device()?;
        let t0 = now_ms();
        let result = f(&mut self.playback).map_err(engine_err);
        self.shared.last_step_ms.set(now_ms() - t0);
        self.sync_checkpoint_bytes();
        self.sync_gpu_timings();
        result
    }

    fn timed_advance(
        &mut self,
        f: impl FnOnce(&mut Playback<GpuEngine>) -> Result<(), EngineError>,
    ) -> Result<bool, JsError> {
        let before = self.playback.current_tick();
        let before_blend = self.playback.tick_blend();
        self.timed_step(f)?;
        Ok(self.playback.current_tick() != before
            || (self.blend_ticks && self.playback.tick_blend() != before_blend))
    }

    async fn frames_async(
        &mut self,
        count: u32,
        width: u32,
        height: u32,
    ) -> Result<Vec<Image>, JsError> {
        self.guard_device()?;
        let count = count.max(1);
        let mut frames = Vec::with_capacity(count as usize);
        for i in 0..count {
            let progress = if count == 1 {
                1.0
            } else {
                i as f32 / (count - 1) as f32
            };
            if progress >= 1.0 {
                self.playback.finish_immediately().map_err(engine_err)?;
            } else {
                self.playback.seek_progress(progress).map_err(engine_err)?;
            }
            let image = self
                .playback
                .simulator()
                .render_async(width, height)
                .await
                .map_err(engine_err)?;
            frames.push(image);
        }
        self.sync_checkpoint_bytes();
        Ok(frames)
    }
}

impl Drop for SceneInstance {
    fn drop(&mut self) {
        self.shared
            .live
            .set(self.shared.live.get().saturating_sub(1));
        let total = self.shared.checkpoint_bytes.get();
        self.shared
            .checkpoint_bytes
            .set((total - self.reported_checkpoint_bytes).max(0.0));
    }
}

#[wasm_bindgen]
impl SceneInstance {
    /// Configures a premultiplied-alpha swapchain on `canvas` at the given
    /// pixel size; wgpu sets the canvas's width/height attributes itself.
    pub fn attach(
        &mut self,
        canvas: web_sys::HtmlCanvasElement,
        width: u32,
        height: u32,
    ) -> Result<(), JsError> {
        self.guard_device()?;
        let surface = self
            .ctx
            .create_canvas_surface(canvas, width, height)
            .map_err(engine_err)?;
        self.surface = Some(surface);
        Ok(())
    }

    pub fn resize(&mut self, width: u32, height: u32) {
        if let Some(surface) = self.surface.as_mut() {
            surface.resize(&self.ctx, width, height);
        }
    }

    #[wasm_bindgen(js_name = setCrop)]
    pub fn set_crop(&mut self, x: f32, y: f32, width: f32, height: f32) -> Result<(), JsError> {
        self.playback
            .simulator()
            .set_crop([x, y, width, height])
            .map_err(engine_err)
    }

    /// Playing frames blend the last two ticks from now on; see `PlayerOptions.blendTicks`.
    #[wasm_bindgen(js_name = enableTickBlending)]
    pub fn enable_tick_blending(&mut self) {
        self.blend_ticks = true;
    }

    /// Pixel size the swapchain is configured at; `null` until `attach`.
    #[wasm_bindgen(js_name = surfaceSize)]
    pub fn surface_size(&self) -> Option<Vec<u32>> {
        self.surface.as_ref().map(|s| {
            let (w, h) = s.size();
            vec![w, h]
        })
    }

    /// Simulation grid side the loaded scene runs at.
    #[wasm_bindgen(js_name = simResolution)]
    pub fn sim_resolution(&self) -> u32 {
        self.playback.scene().sim_resolution.0
    }

    /// Simulation steps the loaded scene's timeline runs.
    #[wasm_bindgen(js_name = totalTicks)]
    pub fn total_ticks(&self) -> u32 {
        self.playback.total_ticks()
    }

    #[wasm_bindgen(js_name = ticksDue)]
    pub fn ticks_due(&self, elapsed_seconds: f32) -> u32 {
        self.playback.ticks_due(elapsed_seconds)
    }

    #[wasm_bindgen(js_name = ticksDueAtProgress)]
    pub fn ticks_due_at_progress(&self, progress: f32) -> u32 {
        self.playback.ticks_due_at_progress(progress)
    }

    #[wasm_bindgen(js_name = currentTick)]
    pub fn current_tick(&self) -> u32 {
        self.playback.current_tick()
    }

    pub fn play(&mut self) {
        self.playback.play();
    }

    pub fn pause(&mut self) {
        self.playback.pause();
    }

    pub fn reset(&mut self) -> Result<(), JsError> {
        self.timed_step(|p| p.reset())
    }

    /// `true` when the call moved the simulation, so the state on screen is
    /// stale; a frame that ran no tick has nothing new to render.
    #[wasm_bindgen(js_name = advanceByElapsed)]
    pub fn advance_by_elapsed(&mut self, seconds: f32) -> Result<bool, JsError> {
        self.timed_advance(|p| p.advance_by_elapsed(seconds))
    }

    /// Drives the reveal from a caller-supplied progress (0..1, clamped) with
    /// a linear progress-to-tick mapping and no internal easing; steps
    /// forward, seeks backwards. Callers apply their own easing first.
    /// Returns whether the simulation moved, as `advanceByElapsed`.
    #[wasm_bindgen(js_name = advanceToProgress)]
    pub fn advance_to_progress(&mut self, progress: f32) -> Result<bool, JsError> {
        self.timed_advance(|p| p.advance_to_progress(progress))
    }

    /// Runs up to `ticks` more steps whatever the play state, finishing (and
    /// drying) at the end of the timeline; `true` once finished. The same
    /// steps as `finishImmediately`, so a host can spread a still's run over
    /// several frames instead of blocking one.
    #[wasm_bindgen(js_name = advanceTicks)]
    pub fn advance_ticks(&mut self, ticks: u32) -> Result<bool, JsError> {
        self.timed_step(|p| p.advance_ticks(ticks))?;
        Ok(self.is_finished())
    }

    /// Swaps the curve `advanceByElapsed`/`seekProgress` map progress with;
    /// `frontLoaded` (default), `linear` or `reveal` (the scene's own
    /// wall-clock paint/settle split at the default paint fraction).
    #[wasm_bindgen(js_name = setProgressCurve)]
    pub fn set_progress_curve(&mut self, curve: &str) -> Result<(), JsError> {
        let curve = match curve {
            "frontLoaded" | "front-loaded" => ProgressCurve::FrontLoaded,
            "linear" => ProgressCurve::Linear,
            "reveal" => {
                ProgressCurve::reveal_for(self.playback.scene(), DEFAULT_PAINT_WALL_FRACTION)
            }
            other => {
                return Err(js_err(
                    "InvalidCurve",
                    format!("{other:?} is not frontLoaded|linear|reveal"),
                ));
            }
        };
        self.playback.set_progress_curve(curve);
        Ok(())
    }

    #[wasm_bindgen(js_name = seekProgress)]
    pub fn seek_progress(&mut self, progress: f32) -> Result<(), JsError> {
        self.timed_step(|p| p.seek_progress(progress))
    }

    #[wasm_bindgen(js_name = tickForProgress)]
    pub fn tick_for_progress(&self, progress: f32) -> u32 {
        self.playback.tick_for_progress(progress)
    }

    /// Steps replayed; see `Playback::seek_towards_tick`.
    #[wasm_bindgen(js_name = seekTowardsTick)]
    pub fn seek_towards_tick(&mut self, target: u32, ticks: u32) -> Result<u32, JsError> {
        let mut replayed = 0;
        self.timed_step(|p| {
            replayed = p.seek_towards_tick(target, ticks)?;
            Ok(())
        })?;
        Ok(replayed)
    }

    #[wasm_bindgen(js_name = finishImmediately)]
    pub fn finish_immediately(&mut self) -> Result<(), JsError> {
        self.timed_step(|p| p.finish_immediately())
    }

    /// See `Playback::go_live`.
    #[wasm_bindgen(js_name = goLive)]
    pub fn go_live(&mut self, ticks_per_second: f32, idle_ticks: u32) -> Result<(), JsError> {
        self.timed_step(|p| p.go_live(ticks_per_second, idle_ticks))
    }

    /// A JSON array of `{ after_ticks, op }`; see `Playback::append`.
    #[wasm_bindgen(js_name = appendOperations)]
    pub fn append_operations(&mut self, json: &str) -> Result<(), JsError> {
        self.guard_device()?;
        let events = parse_appended_json(json).map_err(|e| js_err("InvalidScene", e))?;
        self.playback.append(events).map_err(engine_err)
    }

    pub fn progress(&self) -> f32 {
        self.playback.progress()
    }

    #[wasm_bindgen(js_name = isFinished)]
    pub fn is_finished(&self) -> bool {
        self.playback.state() == PlaybackState::Finished
    }

    #[wasm_bindgen(js_name = isPlaying)]
    pub fn is_playing(&self) -> bool {
        self.playback.state() == PlaybackState::Playing
    }

    /// Presents the current state to the attached canvas. `false` when the
    /// swapchain had no texture this frame.
    pub fn render(&mut self) -> Result<bool, JsError> {
        self.guard_device()?;
        let surface = self
            .surface
            .as_ref()
            .ok_or_else(|| js_err("NoSurface", "attach a canvas before rendering"))?;
        let t0 = now_ms();
        let tick = self.playback.current_tick();
        let blend = self.playback.tick_blend();
        let presented = if self.blend_ticks && self.playback.state() == PlaybackState::Playing {
            self.playback.simulator().present_at(surface, tick, blend)
        } else {
            // A paused or finished scene can hold its canvas indefinitely; its tick images are released.
            self.playback.simulator().clear_interpolation();
            self.playback.simulator().present(surface)
        }
        .map_err(engine_err)?;
        self.shared.last_render_ms.set(now_ms() - t0);
        self.sync_gpu_timings();
        Ok(presented)
    }

    /// PNG of the current state (straight alpha, sRGB), at any size.
    #[wasm_bindgen(js_name = exportPng)]
    pub async fn export_png(
        &mut self,
        width: u32,
        height: u32,
    ) -> Result<js_sys::Uint8Array, JsError> {
        self.guard_device()?;
        let image = self
            .playback
            .simulator()
            .render_async(width, height)
            .await
            .map_err(engine_err)?;
        let bytes = PngExporter.encode(&image).map_err(engine_err)?;
        Ok(js_sys::Uint8Array::from(&bytes[..]))
    }

    /// `count` PNGs evenly spaced in artistic progress, the last finished
    /// and dry. Leaves the playback finished.
    #[wasm_bindgen(js_name = exportFrames)]
    pub async fn export_frames(
        &mut self,
        count: u32,
        width: u32,
        height: u32,
    ) -> Result<js_sys::Array, JsError> {
        let frames = self.frames_async(count, width, height).await?;
        let out = js_sys::Array::new();
        for frame in &frames {
            let bytes = PngExporter.encode(frame).map_err(engine_err)?;
            out.push(&js_sys::Uint8Array::from(&bytes[..]));
        }
        Ok(out)
    }

    /// The baked format: `count` square frames of `size` px stacked
    /// vertically in one PNG (see `bakedManifest` for its manifest).
    #[wasm_bindgen(js_name = exportStrip)]
    pub async fn export_strip(
        &mut self,
        count: u32,
        size: u32,
    ) -> Result<js_sys::Uint8Array, JsError> {
        let frames = self.frames_async(count, size, size).await?;
        let strip =
            scene_tools::stitch_vertical(&frames).map_err(|e| JsError::new(&e.to_string()))?;
        let bytes = PngExporter.encode(&strip).map_err(engine_err)?;
        Ok(js_sys::Uint8Array::from(&bytes[..]))
    }

    #[wasm_bindgen(js_name = checkpointBytes)]
    pub fn checkpoint_bytes(&self) -> f64 {
        self.reported_checkpoint_bytes
    }

    pub fn detach(&mut self) {
        self.surface = None;
    }

    /// Frees the swapchain, checkpoints and simulation buffers and releases
    /// the live-instance slot. The JS object is unusable afterwards.
    pub fn dispose(self) {
        drop(self);
    }
}
