//! Web adapter for the watercolour engine. `scene_tools` is the
//! platform-neutral half (catalogue lookup, intensity, frame strips) that
//! the host-side bake example and tests share; `web` is the wasm-bindgen
//! surface and only exists on `wasm32`.

pub mod scene_tools;

#[cfg(target_arch = "wasm32")]
mod web;

#[cfg(target_arch = "wasm32")]
pub use web::*;
