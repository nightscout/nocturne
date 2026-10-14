//! Watercolour graphics engine, platform-independent half.
//!
//! `domain` holds the model, the CPU reference simulation and the Kubelka-Munk
//! optics; `application` holds the ports and use cases that drive a simulator
//! and renderer implementation. The crate has no dependencies beyond `std` so
//! it can be consumed unchanged by a native (wgpu) adapter, a wasm adapter, or
//! an Android host.

pub mod application;
pub mod domain;
