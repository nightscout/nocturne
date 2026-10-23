//! Compiles the engine's pipelines off the browser's presenting thread.
//!
//! wgpu's webgpu backend creates pipelines with the synchronous
//! `createComputePipeline`/`createRenderPipeline`. Chrome runs those inside
//! the task's command batch on the GPU process main thread, compiling each
//! one (DXC on D3D12) while the compositor waits to present. The `*Async`
//! creates compile on Dawn's worker threads instead, and Dawn de-duplicates
//! shader modules, layouts and pipelines by content, so once async requests
//! for identical descriptors have resolved, wgpu's synchronous creates return
//! the cached objects without compiling. The descriptors here therefore
//! mirror, field for field, what wgpu 30's `backend/webgpu.rs` sends for the
//! same [`super::pipelines`] data, and the [`Warmed`] handles must outlive
//! wgpu's creates: Dawn's caches hold their entries weakly.

use js_sys::{Array, Function, Object, Reflect};
use wasm_bindgen::{JsCast, JsValue};
use wasm_bindgen_futures::JsFuture;

use super::pipelines::{ComputeDesc, FullscreenDesc, LayoutDesc, ModuleDesc, Plan};

/// Requests every pipeline of `plans` asynchronously and resolves once all
/// have compiled. `None` off the WebGPU backend.
pub(super) async fn warm(device: &wgpu::Device, plans: &[&Plan]) -> Option<Warmed> {
    let mut warmup = Warmup::new(device)?;
    // Uncaptured, a failed create here would fault the device; the same
    // failure resurfaces in the caller's own scope.
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    for plan in plans {
        warmup.plan(plan);
    }
    drop(scope.pop());
    Some(warmup.finish().await)
}

struct Warmup {
    device: JsValue,
    keep: Vec<JsValue>,
    pending: Vec<JsFuture>,
}

/// The warmed objects; drop once wgpu has created its own.
pub(super) struct Warmed {
    _keep: Vec<JsValue>,
}

impl Warmup {
    fn new(device: &wgpu::Device) -> Option<Warmup> {
        let device: &JsValue = device.as_webgpu()?.as_ref();
        Some(Warmup {
            device: device.clone(),
            keep: Vec::new(),
            pending: Vec::new(),
        })
    }

    fn call(&self, method: &str, descriptor: &Object) -> Option<JsValue> {
        let function: Function = Reflect::get(&self.device, &JsValue::from_str(method))
            .ok()?
            .dyn_into()
            .ok()?;
        function.call1(&self.device, descriptor).ok()
    }

    fn create(&mut self, method: &str, descriptor: &Object) -> Option<JsValue> {
        let object = self.call(method, descriptor)?;
        self.keep.push(object.clone());
        Some(object)
    }

    fn request(&mut self, method: &str, descriptor: &Object) {
        if let Some(promise) = self
            .call(method, descriptor)
            .and_then(|p| p.dyn_into::<js_sys::Promise>().ok())
        {
            self.pending.push(JsFuture::from(promise));
        }
    }

    fn plan(&mut self, plan: &Plan) {
        let modules: Vec<Option<JsValue>> = plan.modules.iter().map(|m| self.module(m)).collect();
        let layouts: Vec<Option<JsValue>> = plan.layouts.iter().map(|l| self.layout(l)).collect();
        let pair = |module: usize, layout: usize| match (modules.get(module), layouts.get(layout)) {
            (Some(Some(module)), Some(Some(layout))) => Some((module.clone(), layout.clone())),
            _ => None,
        };
        for desc in &plan.compute {
            if let Some((module, layout)) = pair(desc.module, desc.layout) {
                self.compute(desc, &module, &layout);
            }
        }
        for desc in &plan.render {
            if let Some((module, layout)) = pair(desc.module, desc.layout) {
                self.fullscreen(desc.pipeline, &module, &layout, desc.format);
            }
        }
    }

    fn module(&mut self, desc: &ModuleDesc) -> Option<JsValue> {
        let descriptor = object(&[
            ("code", desc.source.as_str().into()),
            ("label", desc.label.into()),
        ]);
        self.create("createShaderModule", &descriptor)
    }

    fn layout(&mut self, desc: &LayoutDesc) -> Option<JsValue> {
        let groups = Array::new();
        for group in &desc.groups {
            let entries = Array::new();
            for entry in &group.entries {
                entries.push(&bind_group_layout_entry(entry)?.into());
            }
            let descriptor = object(&[("entries", entries.into()), ("label", group.label.into())]);
            groups.push(&self.create("createBindGroupLayout", &descriptor)?);
        }
        let descriptor = object(&[
            ("bindGroupLayouts", groups.into()),
            ("label", desc.label.into()),
        ]);
        self.create("createPipelineLayout", &descriptor)
    }

    fn compute(&mut self, desc: &ComputeDesc, module: &JsValue, layout: &JsValue) {
        let stage = object(&[
            ("module", module.clone()),
            ("entryPoint", desc.entry.into()),
        ]);
        let descriptor = object(&[
            ("layout", layout.clone()),
            ("compute", stage.into()),
            ("label", desc.entry.into()),
        ]);
        self.request("createComputePipelineAsync", &descriptor);
    }

    fn fullscreen(
        &mut self,
        desc: &FullscreenDesc,
        module: &JsValue,
        layout: &JsValue,
        format: wgpu::TextureFormat,
    ) {
        let Some(format) = format_name(format) else {
            return;
        };
        let vertex = object(&[
            ("module", module.clone()),
            ("entryPoint", desc.vertex.into()),
            ("buffers", Array::new().into()),
        ]);
        let target = object(&[
            ("format", format.into()),
            ("writeMask", wgpu::ColorWrites::ALL.bits().into()),
        ]);
        let fragment = object(&[
            ("module", module.clone()),
            ("targets", Array::of1(&target).into()),
            ("entryPoint", desc.fragment.into()),
        ]);
        let multisample = object(&[
            ("count", 1u32.into()),
            ("mask", u32::MAX.into()),
            ("alphaToCoverageEnabled", false.into()),
        ]);
        let primitive = object(&[
            ("cullMode", "none".into()),
            ("frontFace", "ccw".into()),
            ("topology", "triangle-list".into()),
            ("unclippedDepth", false.into()),
        ]);
        let descriptor = object(&[
            ("layout", layout.clone()),
            ("vertex", vertex.into()),
            ("label", desc.label.into()),
            ("fragment", fragment.into()),
            ("multisample", multisample.into()),
            ("primitive", primitive.into()),
        ]);
        self.request("createRenderPipelineAsync", &descriptor);
    }

    /// Awaits every pipeline request; a rejected one is left to wgpu's
    /// synchronous create to report inside its error scope.
    async fn finish(mut self) -> Warmed {
        for pending in std::mem::take(&mut self.pending) {
            if let Ok(pipeline) = pending.await {
                self.keep.push(pipeline);
            }
        }
        Warmed { _keep: self.keep }
    }
}

/// `navigator.gpu.getPreferredCanvasFormat()`, the format a canvas surface
/// is configured with (`PresentSurface::new` takes the first capability,
/// which wgpu sorts to the preferred one).
pub(super) fn preferred_canvas_format() -> Option<wgpu::TextureFormat> {
    let navigator = Reflect::get(&js_sys::global(), &JsValue::from_str("navigator")).ok()?;
    let gpu = Reflect::get(&navigator, &JsValue::from_str("gpu")).ok()?;
    let preferred: Function = Reflect::get(&gpu, &JsValue::from_str("getPreferredCanvasFormat"))
        .ok()?
        .dyn_into()
        .ok()?;
    match preferred.call0(&gpu).ok()?.as_string()?.as_str() {
        "bgra8unorm" => Some(wgpu::TextureFormat::Bgra8Unorm),
        "rgba8unorm" => Some(wgpu::TextureFormat::Rgba8Unorm),
        _ => None,
    }
}

/// Resolves in a later task (`setTimeout(0)`), so the device calls made
/// before it reach the GPU process as a command batch of their own.
pub(super) async fn next_task() {
    let promise = js_sys::Promise::new(&mut |resolve, _reject| {
        let global = js_sys::global();
        let scheduled = Reflect::get(&global, &JsValue::from_str("setTimeout"))
            .ok()
            .and_then(|f| f.dyn_into::<Function>().ok())
            .is_some_and(|set_timeout| {
                set_timeout
                    .call2(&global, &resolve, &JsValue::from(0))
                    .is_ok()
            });
        if !scheduled {
            let _ = resolve.call0(&JsValue::UNDEFINED);
        }
    });
    let _ = JsFuture::from(promise).await;
}

fn format_name(format: wgpu::TextureFormat) -> Option<&'static str> {
    match format {
        wgpu::TextureFormat::Bgra8Unorm => Some("bgra8unorm"),
        wgpu::TextureFormat::Rgba8Unorm => Some("rgba8unorm"),
        wgpu::TextureFormat::Rgba16Float => Some("rgba16float"),
        _ => None,
    }
}

fn object(fields: &[(&str, JsValue)]) -> Object {
    let object = Object::new();
    for (key, value) in fields {
        let _ = Reflect::set(&object, &JsValue::from_str(key), value);
    }
    object
}

/// `None` for a binding type the engine does not use.
fn bind_group_layout_entry(entry: &wgpu::BindGroupLayoutEntry) -> Option<Object> {
    let mapped = object(&[
        ("binding", entry.binding.into()),
        ("visibility", entry.visibility.bits().into()),
    ]);
    match entry.ty {
        wgpu::BindingType::Buffer {
            ty,
            has_dynamic_offset,
            min_binding_size,
        } => {
            let buffer = object(&[
                ("hasDynamicOffset", has_dynamic_offset.into()),
                (
                    "type",
                    match ty {
                        wgpu::BufferBindingType::Uniform => "uniform",
                        wgpu::BufferBindingType::Storage { read_only: false } => "storage",
                        wgpu::BufferBindingType::Storage { read_only: true } => "read-only-storage",
                    }
                    .into(),
                ),
            ]);
            if let Some(size) = min_binding_size {
                let _ = Reflect::set(
                    &buffer,
                    &JsValue::from_str("minBindingSize"),
                    &JsValue::from_f64(size.get() as f64),
                );
            }
            let _ = Reflect::set(&mapped, &JsValue::from_str("buffer"), &buffer);
        }
        wgpu::BindingType::Texture {
            sample_type,
            view_dimension,
            multisampled,
        } => {
            let sample_type = match sample_type {
                wgpu::TextureSampleType::Float { filterable: true } => "float",
                wgpu::TextureSampleType::Float { filterable: false } => "unfilterable-float",
                wgpu::TextureSampleType::Depth => "depth",
                wgpu::TextureSampleType::Sint => "sint",
                wgpu::TextureSampleType::Uint => "uint",
            };
            let view_dimension = match view_dimension {
                wgpu::TextureViewDimension::D1 => "1d",
                wgpu::TextureViewDimension::D2 => "2d",
                wgpu::TextureViewDimension::D2Array => "2d-array",
                wgpu::TextureViewDimension::Cube => "cube",
                wgpu::TextureViewDimension::CubeArray => "cube-array",
                wgpu::TextureViewDimension::D3 => "3d",
            };
            let texture = object(&[
                ("multisampled", multisampled.into()),
                ("sampleType", sample_type.into()),
                ("viewDimension", view_dimension.into()),
            ]);
            let _ = Reflect::set(&mapped, &JsValue::from_str("texture"), &texture);
        }
        _ => return None,
    }
    Some(mapped)
}
