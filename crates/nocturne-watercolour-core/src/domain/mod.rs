//! Pure model and reference algorithms. Nothing in here performs I/O or knows
//! about a GPU, a serialisation format or the application layer.

pub mod grid;
pub mod image;
pub mod ops;
pub mod optics;
pub mod paint;
pub mod palette;
pub mod paper;
pub mod pigment;
pub mod scene;
pub mod seed;
pub mod sim;
pub mod swirl;
pub mod timeline;

pub use grid::SimulationGrid;
pub use image::Image;
pub use ops::{
    BrushStroke, LiftStroke, MAX_SETTLE_SHARE, Mask, Operation, Point, RadiusProfile, StrokeSpan,
    WaterStroke,
};
pub use optics::CompositeMode;
pub use palette::{Palette, PaletteEntry, PigmentRole};
pub use paper::{Paper, PaperField};
pub use pigment::{Pigment, Rgb};
pub use scene::{
    Background, Scene, SceneId, SimResolution, SizeHint, ValidationError, isotropic_scale,
};
pub use seed::{Seed, SubSeed};
pub use timeline::{Timeline, TimelineEvent};
