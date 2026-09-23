//! wasm-bindgen surface. One `WatercolourEngine` per page owns the WebGPU
//! device and the compiled pipelines; each `SceneInstance` forks those
//! pipelines for its own scene and canvas. Every error crosses to JS as an
//! `Error` whose message is `Code: detail` (see `scene_tools::SceneToolError`
//! for the scene codes; the engine adds `WebGpuUnavailable`, `InitFailed`,
//! `InstanceLimit`, `DeviceLost`, `NoSurface` and `Engine`).

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use nocturne_watercolour_core::application::playback::DEFAULT_PAINT_WALL_FRACTION;
use nocturne_watercolour_core::application::{
    EngineError, Exporter, Playback, PlaybackState, ProgressCurve,
};
use nocturne_watercolour_core::domain::{Image, Seed};
use nocturne_watercolour_infra::document::parse_scene_json;
use nocturne_watercolour_infra::export::PngExporter;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine, PresentSurface};
use wasm_bindgen::prelude::*;

use crate::scene_tools::{self, BakedManifest, Surface};

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
  initMs: number;
  adapterName: string;
}
"#;

thread_local! {
    static LOST_CALLBACKS: RefCell<Vec<js_sys::Function>> = const { RefCell::new(Vec::new()) };
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
    init_ms: Cell<f64>,
}

#[wasm_bindgen]
pub struct WatercolourEngine {
    ctx: GpuContext,
    template: GpuEngine,
    shared: Rc<Shared>,
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
        ctx.on_device_lost(|message| {
            LOST_CALLBACKS.with(|cbs| {
                for cb in cbs.borrow().iter() {
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
    /// seek-checkpoint budget; anything below one checkpoint still keeps the
    /// one at tick 0, so `seek` falls back to replaying from the start.
    #[wasm_bindgen(js_name = createInstance)]
    pub fn create_instance(
        &self,
        scene_json: &str,
        duration_ms: f64,
        settle_fraction: f64,
        paint_wall_fraction: f64,
        checkpoint_budget_bytes: Option<f64>,
    ) -> Result<SceneInstance, JsError> {
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
        let mut scene = parse_scene_json(scene_json).map_err(|e| js_err("InvalidScene", e))?;
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
        LOST_CALLBACKS.with(|cbs| cbs.borrow_mut().push(callback));
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

    /// Timings are CPU-side (submission, not GPU completion) in
    /// `performance.now()` milliseconds.
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
    let seed = Seed(if seed.is_finite() {
        seed.max(0.0) as u64
    } else {
        0
    });
    let detail = scene_tools::parse_detail(detail).ok_or_else(|| {
        js_err(
            "InvalidDetail",
            format!("{detail:?} is not small|medium|large|extralarge"),
        )
    })?;
    let surface = Surface::parse(surface)
        .ok_or_else(|| js_err("InvalidSurface", format!("{surface:?} is not light|dark")))?;
    let sim = if sim_resolution.is_finite() && sim_resolution > 0.0 {
        Some(sim_resolution as u32)
    } else {
        None
    };
    scene_tools::catalogue_scene_json_with_resolution(
        artwork_id, seed, palette, intensity, detail, surface, sim,
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
    let seed = Seed(if seed.is_finite() {
        seed.max(0.0) as u64
    } else {
        0
    });
    let detail = scene_tools::parse_detail(detail).ok_or_else(|| {
        js_err(
            "InvalidDetail",
            format!("{detail:?} is not small|medium|large|extralarge"),
        )
    })?;
    let surface = Surface::parse(surface)
        .ok_or_else(|| js_err("InvalidSurface", format!("{surface:?} is not light|dark")))?;
    let sim = if sim_resolution.is_finite() && sim_resolution > 0.0 {
        Some(sim_resolution as u32)
    } else {
        None
    };
    scene_tools::icon_scene_json(
        elements_json,
        name,
        seed,
        palette,
        intensity,
        detail,
        surface,
        sim,
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

    fn timed_step(
        &mut self,
        f: impl FnOnce(&mut Playback<GpuEngine>) -> Result<(), EngineError>,
    ) -> Result<(), JsError> {
        self.guard_device()?;
        let t0 = now_ms();
        let result = f(&mut self.playback).map_err(engine_err);
        self.shared.last_step_ms.set(now_ms() - t0);
        self.sync_checkpoint_bytes();
        result
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

    pub fn play(&mut self) {
        self.playback.play();
    }

    pub fn pause(&mut self) {
        self.playback.pause();
    }

    pub fn reset(&mut self) -> Result<(), JsError> {
        self.timed_step(|p| p.reset())
    }

    #[wasm_bindgen(js_name = advanceByElapsed)]
    pub fn advance_by_elapsed(&mut self, seconds: f32) -> Result<(), JsError> {
        self.timed_step(|p| p.advance_by_elapsed(seconds))
    }

    /// Drives the reveal from a caller-supplied progress (0..1, clamped) with
    /// a linear progress-to-tick mapping and no internal easing; steps
    /// forward, seeks backwards. Callers apply their own easing first.
    #[wasm_bindgen(js_name = advanceToProgress)]
    pub fn advance_to_progress(&mut self, progress: f32) -> Result<(), JsError> {
        self.timed_step(|p| p.advance_to_progress(progress))
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

    #[wasm_bindgen(js_name = finishImmediately)]
    pub fn finish_immediately(&mut self) -> Result<(), JsError> {
        self.timed_step(|p| p.finish_immediately())
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
        let presented = self
            .playback
            .simulator()
            .present(surface)
            .map_err(engine_err)?;
        self.shared.last_render_ms.set(now_ms() - t0);
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
