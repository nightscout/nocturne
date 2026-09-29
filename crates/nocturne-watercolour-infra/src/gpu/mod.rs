//! wgpu 30 backend. WGSL compute passes mirror `domain::sim` one entry point
//! per CPU `pass_*` function; the shaders in `shaders/` document which rule
//! each implements. Device, queue, buffers and swapchains never leave this
//! module.

mod context;
mod engine;
mod layout;
mod surface;
mod timer;

pub use context::GpuContext;
pub use engine::{BLUR_MAX_RADIUS, CHECKPOINT_BUDGET_BYTES, CommandCounts, GpuEngine, GpuTimings};
pub use layout::StateLayout;
pub use surface::PresentSurface;
