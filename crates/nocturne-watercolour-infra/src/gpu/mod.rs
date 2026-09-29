//! wgpu 30 backend. WGSL compute passes mirror `domain::sim` one entry point
//! per CPU `pass_*` function; the shaders in `shaders/` document which rule
//! each implements. Device, queue, buffers and swapchains never leave this
//! module.

mod context;
mod engine;
mod layout;
mod surface;

pub use context::GpuContext;
pub use engine::{CHECKPOINT_BUDGET_BYTES, CommandCounts, GpuEngine};
pub use layout::StateLayout;
pub use surface::PresentSurface;
