//! Ports and use cases. Infrastructure implements the ports; hosts call the
//! use cases.

pub mod choreography;
pub mod cpu;
pub mod playback;
pub mod ports;
pub mod reveal;
pub mod use_cases;

pub use choreography::{Choreography, choreograph};
pub use cpu::CpuEngine;
pub use playback::{CheckpointPolicy, Playback, PlaybackState, ProgressCurve};
pub use ports::{CheckpointId, EngineError, Exporter, Renderer, Simulator};
pub use reveal::{
    Reveal, apply_settle_fraction, settle_after_last_stroke, settle_rate_for,
    settle_share_for_ticks,
};
pub use use_cases::{
    Advance, AdvanceByElapsed, ApplyOperation, CreateScene, ExportFinished, SceneUpdate,
    UpdateScene,
};
