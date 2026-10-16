//! A presentable surface (browser canvas or native window) configured on
//! the shared device. The engine's `present` draws the current frame into
//! it; nothing else touches the swapchain.

use nocturne_watercolour_core::application::EngineError;

use super::context::GpuContext;

pub struct PresentSurface {
    surface: wgpu::Surface<'static>,
    config: wgpu::SurfaceConfiguration,
}

impl PresentSurface {
    pub(super) fn new(
        ctx: &GpuContext,
        surface: wgpu::Surface<'static>,
        width: u32,
        height: u32,
    ) -> Result<PresentSurface, EngineError> {
        let caps = surface.get_capabilities(ctx.adapter());
        let format = *caps
            .formats
            .first()
            .ok_or_else(|| EngineError::new("surface reports no supported formats"))?;
        // The web backend advertises only `Opaque` in `alpha_modes` yet
        // configures `PreMultiplied` fine (it maps to the canvas
        // `premultiplied` alpha mode); the artwork must sit transparently
        // over the page, so prefer it wherever the platform can take it.
        let alpha_mode = if cfg!(target_arch = "wasm32")
            || caps
                .alpha_modes
                .contains(&wgpu::CompositeAlphaMode::PreMultiplied)
        {
            wgpu::CompositeAlphaMode::PreMultiplied
        } else {
            wgpu::CompositeAlphaMode::Auto
        };
        let present_mode = caps
            .present_modes
            .first()
            .copied()
            .unwrap_or(wgpu::PresentMode::Fifo);
        let config = wgpu::SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format,
            color_space: wgpu::SurfaceColorSpace::Auto,
            width: width.max(1),
            height: height.max(1),
            present_mode,
            desired_maximum_frame_latency: 2,
            alpha_mode,
            view_formats: vec![],
        };
        surface.configure(ctx.device(), &config);
        Ok(PresentSurface { surface, config })
    }

    pub fn resize(&mut self, ctx: &GpuContext, width: u32, height: u32) {
        let (w, h) = (width.max(1), height.max(1));
        if (w, h) == (self.config.width, self.config.height) {
            return;
        }
        self.config.width = w;
        self.config.height = h;
        self.surface.configure(ctx.device(), &self.config);
    }

    pub fn size(&self) -> (u32, u32) {
        (self.config.width, self.config.height)
    }

    pub(super) fn format(&self) -> wgpu::TextureFormat {
        self.config.format
    }

    /// Whether the presentation shader must apply the sRGB transfer curve
    /// itself. Browsers only offer `bgra8unorm`/`rgba8unorm` canvases, whose
    /// bytes the compositor reads as already-encoded sRGB, so there the
    /// answer is yes; an `*-srgb` swapchain encodes in hardware.
    pub fn encodes_srgb_in_shader(&self) -> bool {
        !self.config.format.is_srgb()
    }

    pub(super) fn acquire(
        &self,
        ctx: &GpuContext,
    ) -> Result<Option<wgpu::SurfaceTexture>, EngineError> {
        match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(t)
            | wgpu::CurrentSurfaceTexture::Suboptimal(t) => Ok(Some(t)),
            wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => {
                Ok(None)
            }
            wgpu::CurrentSurfaceTexture::Outdated => {
                self.surface.configure(ctx.device(), &self.config);
                Ok(None)
            }
            wgpu::CurrentSurfaceTexture::Lost => Err(EngineError::new("surface lost")),
            wgpu::CurrentSurfaceTexture::Validation => {
                Err(EngineError::new("surface validation error"))
            }
        }
    }
}
