//! Shader modules, layouts and pipelines as data, created through wgpu here
//! and, in the browser, requested first from the raw `GPUDevice` by
//! [`super::warm`] from the same descriptors.

pub(super) struct ModuleDesc {
    pub label: &'static str,
    pub source: String,
}

pub(super) struct GroupDesc {
    pub label: &'static str,
    pub entries: Vec<wgpu::BindGroupLayoutEntry>,
}

/// A pipeline layout and the bind group layouts it is made of, one per group.
pub(super) struct LayoutDesc {
    pub label: &'static str,
    pub groups: Vec<GroupDesc>,
}

/// A compute pipeline labelled with its entry point; `module` and `layout`
/// index the plan it belongs to.
pub(super) struct ComputeDesc {
    pub entry: &'static str,
    pub module: usize,
    pub layout: usize,
}

/// A vertex-buffer-free render pipeline into one colour target, no blending.
pub(super) struct FullscreenDesc {
    pub label: &'static str,
    pub vertex: &'static str,
    pub fragment: &'static str,
}

// Only the browser warm-up reads more than `format`.
#[cfg_attr(not(target_arch = "wasm32"), allow(dead_code))]
pub(super) struct RenderDesc {
    pub pipeline: &'static FullscreenDesc,
    pub module: usize,
    pub layout: usize,
    pub format: wgpu::TextureFormat,
}

/// Modules, layouts and the pipelines built from them, which refer to the
/// modules and layouts by index.
#[derive(Default)]
pub(super) struct Plan {
    pub modules: Vec<ModuleDesc>,
    pub layouts: Vec<LayoutDesc>,
    pub compute: Vec<ComputeDesc>,
    pub render: Vec<RenderDesc>,
}

impl ModuleDesc {
    pub fn create(&self, device: &wgpu::Device) -> wgpu::ShaderModule {
        device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some(self.label),
            source: wgpu::ShaderSource::Wgsl(self.source.as_str().into()),
        })
    }
}

impl LayoutDesc {
    pub fn create(
        &self,
        device: &wgpu::Device,
    ) -> (Vec<wgpu::BindGroupLayout>, wgpu::PipelineLayout) {
        let groups: Vec<wgpu::BindGroupLayout> = self
            .groups
            .iter()
            .map(|group| {
                device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some(group.label),
                    entries: &group.entries,
                })
            })
            .collect();
        let refs: Vec<Option<&wgpu::BindGroupLayout>> = groups.iter().map(Some).collect();
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some(self.label),
            bind_group_layouts: &refs,
            immediate_size: 0,
        });
        (groups, layout)
    }
}

impl ComputeDesc {
    pub fn create(
        &self,
        device: &wgpu::Device,
        module: &wgpu::ShaderModule,
        layout: &wgpu::PipelineLayout,
    ) -> wgpu::ComputePipeline {
        device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some(self.entry),
            layout: Some(layout),
            module,
            entry_point: Some(self.entry),
            compilation_options: Default::default(),
            cache: None,
        })
    }
}

impl FullscreenDesc {
    pub fn create(
        &self,
        device: &wgpu::Device,
        module: &wgpu::ShaderModule,
        layout: &wgpu::PipelineLayout,
        format: wgpu::TextureFormat,
    ) -> wgpu::RenderPipeline {
        device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some(self.label),
            layout: Some(layout),
            vertex: wgpu::VertexState {
                module,
                entry_point: Some(self.vertex),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            fragment: Some(wgpu::FragmentState {
                module,
                entry_point: Some(self.fragment),
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
}
