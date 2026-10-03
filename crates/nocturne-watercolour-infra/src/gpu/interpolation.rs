pub(super) struct Interpolation {
    pub width: u32,
    pub height: u32,
    pub format: wgpu::TextureFormat,
    tick: Option<u32>,
    reset_previous: bool,
    textures: [wgpu::Texture; 2],
    views: [wgpu::TextureView; 2],
    current: usize,
    uniform: wgpu::Buffer,
    bind_groups: [wgpu::BindGroup; 2],
    pipeline: wgpu::RenderPipeline,
}

impl Interpolation {
    pub fn new(
        device: &wgpu::Device,
        width: u32,
        height: u32,
        format: wgpu::TextureFormat,
    ) -> Self {
        let texture = || {
            device.create_texture(&wgpu::TextureDescriptor {
                label: Some("tick-image"),
                size: wgpu::Extent3d {
                    width,
                    height,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::Rgba16Float,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                    | wgpu::TextureUsages::TEXTURE_BINDING
                    | wgpu::TextureUsages::COPY_SRC
                    | wgpu::TextureUsages::COPY_DST,
                view_formats: &[],
            })
        };
        let texture_entry = |binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: false },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("tick-blend"),
            entries: &[
                texture_entry(0),
                texture_entry(1),
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
            ],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("tick-blend"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("tick-blend"),
            source: wgpu::ShaderSource::Wgsl(
                format!(
                    "{}\n{}",
                    include_str!("shaders/canvas.wgsl"),
                    include_str!("shaders/interpolation.wgsl")
                )
                .into(),
            ),
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("tick-blend"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vertex"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fragment"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        let textures = [texture(), texture()];
        let views = textures
            .each_ref()
            .map(|texture| texture.create_view(&Default::default()));
        let uniform = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("tick-blend"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let bind_groups = [0, 1].map(|current| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("tick-blend"),
                layout: &layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&views[current ^ 1]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&views[current]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: uniform.as_entire_binding(),
                    },
                ],
            })
        });
        Self {
            width,
            height,
            format,
            tick: None,
            reset_previous: true,
            textures,
            views,
            current: 0,
            uniform,
            bind_groups,
            pipeline,
        }
    }

    pub fn advance(&mut self, tick: u32) -> bool {
        if self.tick == Some(tick) {
            return false;
        }
        if self.tick.is_some() {
            self.current ^= 1;
        }
        self.reset_previous = self.tick.and_then(|previous| previous.checked_add(1)) != Some(tick);
        true
    }

    pub fn current_view(&self) -> &wgpu::TextureView {
        &self.views[self.current]
    }

    pub fn encode(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        view: &wgpu::TextureView,
        tick: u32,
        blend: f32,
        encode_srgb: bool,
    ) -> wgpu::CommandBuffer {
        queue.write_buffer(
            &self.uniform,
            0,
            bytemuck::cast_slice(&[blend, if encode_srgb { 1.0 } else { 0.0 }, 0.0, 0.0]),
        );
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("tick-blend"),
        });
        if self.reset_previous {
            encoder.copy_texture_to_texture(
                self.textures[self.current].as_image_copy(),
                self.textures[self.current ^ 1].as_image_copy(),
                wgpu::Extent3d {
                    width: self.width,
                    height: self.height,
                    depth_or_array_layers: 1,
                },
            );
        }
        self.tick = Some(tick);
        self.reset_previous = false;
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("tick-blend"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, &self.bind_groups[self.current], &[]);
            pass.draw(0..3, 0..1);
        }
        encoder.finish()
    }
}
