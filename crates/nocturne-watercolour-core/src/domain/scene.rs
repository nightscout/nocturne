//! A complete, validated description of an artwork.

use super::ops::{MAX_SETTLE_SHARE, Mask, Operation, Point};
use super::optics::CompositeMode;
use super::palette::{MAX_PIGMENTS, Palette};
use super::paper::Paper;
use super::seed::Seed;
use super::timeline::Timeline;

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct SceneId(pub String);

/// Intended output size; rendering may use any size. Its aspect is what
/// the square simulation grid is stretched to, so it also fixes the metric
/// paper grain and stamps are measured in (see [`isotropic_scale`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SizeHint {
    pub width: u32,
    pub height: u32,
}

impl SizeHint {
    /// `width / height`; `1` when either side is zero.
    pub fn aspect(self) -> f32 {
        if self.width == 0 || self.height == 0 {
            1.0
        } else {
            self.width as f32 / self.height as f32
        }
    }
}

/// Per-axis factors that turn normalised scene coordinates into an
/// isotropic metric: the shorter side spans `0..1`, the longer side
/// `0..max(aspect, 1/aspect)`. A distance of `d` in this metric is the same
/// number of output pixels along either axis, so radii, feathers and paper
/// grain expressed in it come out round after the square grid is stretched
/// to the size hint. Returns `(x_factor, y_factor)`.
pub fn isotropic_scale(aspect: f32) -> (f32, f32) {
    let a = if aspect.is_finite() && aspect > 0.0 {
        aspect
    } else {
        1.0
    };
    (a.max(1.0), (1.0 / a).max(1.0))
}

/// Square simulation grid side, bounded to keep every backend's memory and
/// per-tick cost predictable.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SimResolution(pub u32);

impl SimResolution {
    pub const MIN: u32 = 64;
    pub const MAX: u32 = 512;
}

/// The surface the transparent output is destined for. It selects the
/// compositing mode (see `optics::CompositeMode`): a light host gets the
/// physically motivated subtractive conversion, a dark host the luminous
/// display conversion.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Background {
    #[default]
    Transparent,
    TransparentOnDark,
}

impl Background {
    pub fn composite_mode(self) -> CompositeMode {
        match self {
            Background::Transparent => CompositeMode::Subtractive,
            Background::TransparentOnDark => CompositeMode::Luminous,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Scene {
    pub id: SceneId,
    pub size_hint: SizeHint,
    pub paper: Paper,
    pub palette: Palette,
    pub timeline: Timeline,
    pub seed: Seed,
    pub sim_resolution: SimResolution,
    pub background: Background,
}

/// Bounds on per-operation amounts; beyond these the simulation's clamps
/// dominate and the result stops responding to the input.
pub const MAX_CONCENTRATION: f32 = 4.0;
pub const MAX_WATER: f32 = 4.0;
pub const MAX_RADIUS: f32 = 1.0;
pub const MAX_DRY_RATE: f32 = 64.0;
pub const MAX_TOTAL_TICKS: u32 = 20_000;

#[derive(Debug, Clone, PartialEq)]
pub enum ValidationError {
    EmptyId,
    EmptyTimeline,
    ZeroTotalTicks,
    TotalTicksTooLarge {
        total: u32,
        max: u32,
    },
    EventAfterEnd {
        index: usize,
        at_tick: u32,
        total: u32,
    },
    SimResolutionOutOfRange {
        value: u32,
        min: u32,
        max: u32,
    },
    EmptyPalette,
    TooManyPigments {
        count: usize,
        max: usize,
    },
    PigmentIndexOutOfRange {
        event: usize,
        index: usize,
        count: usize,
    },
    EmptyPath {
        event: usize,
    },
    PointOutOfRange {
        event: usize,
        point: usize,
    },
    NotFinite {
        event: Option<usize>,
        field: &'static str,
    },
    OutOfRange {
        event: Option<usize>,
        field: &'static str,
        min: f32,
        max: f32,
    },
    SizeHintZero,
}

impl std::fmt::Display for ValidationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for ValidationError {}

impl Scene {
    pub fn composite_mode(&self) -> CompositeMode {
        self.background.composite_mode()
    }

    /// Output aspect the simulation grid is stretched to; see [`isotropic_scale`].
    pub fn aspect(&self) -> f32 {
        self.size_hint.aspect()
    }

    /// Every problem found, not just the first, so an authoring tool can
    /// report them together.
    pub fn validate(&self) -> Result<(), Vec<ValidationError>> {
        let mut errors = Vec::new();
        if self.id.0.is_empty() {
            errors.push(ValidationError::EmptyId);
        }
        if self.size_hint.width == 0 || self.size_hint.height == 0 {
            errors.push(ValidationError::SizeHintZero);
        }
        let res = self.sim_resolution.0;
        if !(SimResolution::MIN..=SimResolution::MAX).contains(&res) {
            errors.push(ValidationError::SimResolutionOutOfRange {
                value: res,
                min: SimResolution::MIN,
                max: SimResolution::MAX,
            });
        }
        if self.palette.is_empty() {
            errors.push(ValidationError::EmptyPalette);
        }
        if self.palette.len() > MAX_PIGMENTS {
            errors.push(ValidationError::TooManyPigments {
                count: self.palette.len(),
                max: MAX_PIGMENTS,
            });
        }
        self.validate_paper(&mut errors);
        if self.timeline.is_empty() {
            errors.push(ValidationError::EmptyTimeline);
        }
        if self.timeline.total_ticks == 0 {
            errors.push(ValidationError::ZeroTotalTicks);
        }
        if self.timeline.total_ticks > MAX_TOTAL_TICKS {
            errors.push(ValidationError::TotalTicksTooLarge {
                total: self.timeline.total_ticks,
                max: MAX_TOTAL_TICKS,
            });
        }
        for (i, ev) in self.timeline.events.iter().enumerate() {
            if ev.at_tick > self.timeline.total_ticks {
                errors.push(ValidationError::EventAfterEnd {
                    index: i,
                    at_tick: ev.at_tick,
                    total: self.timeline.total_ticks,
                });
            }
            self.validate_op(i, &ev.op, &mut errors);
        }
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors)
        }
    }

    fn validate_paper(&self, errors: &mut Vec<ValidationError>) {
        let p = &self.paper;
        check_range(
            errors,
            None,
            "paper.grain_scale",
            p.grain_scale,
            1.0,
            1024.0,
        );
        check_range(
            errors,
            None,
            "paper.height_amplitude",
            p.height_amplitude,
            0.0,
            1.0,
        );
        check_range(
            errors,
            None,
            "paper.absorbency.min",
            p.absorbency[0],
            0.0,
            1.0,
        );
        check_range(
            errors,
            None,
            "paper.absorbency.max",
            p.absorbency[1],
            0.0,
            1.0,
        );
        if p.absorbency[0] > p.absorbency[1] {
            errors.push(ValidationError::OutOfRange {
                event: None,
                field: "paper.absorbency.min",
                min: 0.0,
                max: p.absorbency[1],
            });
        }
        check_range(
            errors,
            None,
            "paper.fibre_anisotropy",
            p.fibre_anisotropy,
            0.0,
            1.0,
        );
    }

    fn validate_op(&self, event: usize, op: &Operation, errors: &mut Vec<ValidationError>) {
        let ev = Some(event);
        if let Some(path) = op.path() {
            if path.is_empty() {
                errors.push(ValidationError::EmptyPath { event });
            }
            for (pi, p) in path.iter().enumerate() {
                if !p.x.is_finite() || !p.y.is_finite() {
                    errors.push(ValidationError::NotFinite {
                        event: ev,
                        field: "path",
                    });
                } else if !in_unit(*p) {
                    errors.push(ValidationError::PointOutOfRange { event, point: pi });
                }
            }
        }
        match op {
            Operation::Dab(s) => {
                if s.pigment >= self.palette.len() {
                    errors.push(ValidationError::PigmentIndexOutOfRange {
                        event,
                        index: s.pigment,
                        count: self.palette.len(),
                    });
                }
                check_range(errors, ev, "radius", s.radius, 0.0, MAX_RADIUS);
                check_range(
                    errors,
                    ev,
                    "concentration",
                    s.concentration,
                    0.0,
                    MAX_CONCENTRATION,
                );
                check_range(errors, ev, "water", s.water, 0.0, MAX_WATER);
                check_range(errors, ev, "softness", s.softness, 0.0, 1.0);
            }
            Operation::Brush(s) => {
                if s.pigment >= self.palette.len() {
                    errors.push(ValidationError::PigmentIndexOutOfRange {
                        event,
                        index: s.pigment,
                        count: self.palette.len(),
                    });
                }
                check_range(errors, ev, "radius.start", s.radius.start, 0.0, MAX_RADIUS);
                check_range(errors, ev, "radius.end", s.radius.end, 0.0, MAX_RADIUS);
                check_range(
                    errors,
                    ev,
                    "concentration",
                    s.concentration,
                    0.0,
                    MAX_CONCENTRATION,
                );
                check_range(errors, ev, "water", s.water, 0.0, MAX_WATER);
                check_range(errors, ev, "softness", s.softness, 0.0, 1.0);
            }
            Operation::Water(s) => {
                check_range(errors, ev, "radius.start", s.radius.start, 0.0, MAX_RADIUS);
                check_range(errors, ev, "radius.end", s.radius.end, 0.0, MAX_RADIUS);
                check_range(errors, ev, "water", s.water, 0.0, MAX_WATER);
                check_range(errors, ev, "softness", s.softness, 0.0, 1.0);
            }
            Operation::Lift(s) => {
                check_range(errors, ev, "radius.start", s.radius.start, 0.0, MAX_RADIUS);
                check_range(errors, ev, "radius.end", s.radius.end, 0.0, MAX_RADIUS);
                check_range(errors, ev, "strength", s.strength, 0.0, 1.0);
                check_range(errors, ev, "softness", s.softness, 0.0, 1.0);
            }
            Operation::Dry { rate } => {
                check_range(errors, ev, "rate", *rate, 0.0, MAX_DRY_RATE);
            }
            Operation::SetMask(m) => {
                check_range(errors, ev, "feather", m.feather(), 0.0, 1.0);
                if let Mask::Path { radius, .. } = m {
                    check_range(errors, ev, "radius", *radius, 0.0, MAX_RADIUS);
                }
            }
            Operation::Settle { share } => {
                check_range(errors, ev, "share", *share, 0.0, MAX_SETTLE_SHARE);
            }
            Operation::DryAll | Operation::ClearMask => {}
        }
    }
}

fn in_unit(p: Point) -> bool {
    (0.0..=1.0).contains(&p.x) && (0.0..=1.0).contains(&p.y)
}

fn check_range(
    errors: &mut Vec<ValidationError>,
    event: Option<usize>,
    field: &'static str,
    value: f32,
    min: f32,
    max: f32,
) {
    if !value.is_finite() {
        errors.push(ValidationError::NotFinite { event, field });
    } else if value < min || value > max {
        errors.push(ValidationError::OutOfRange {
            event,
            field,
            min,
            max,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::ops::{BrushStroke, RadiusProfile, StrokeSpan};

    fn valid_scene() -> Scene {
        let seed = Seed(3);
        let mut timeline = Timeline::new(120);
        timeline.push(
            0,
            Operation::Brush(BrushStroke {
                path: vec![Point::new(0.2, 0.5), Point::new(0.8, 0.5)],
                radius: RadiusProfile::uniform(0.1),
                pigment: 0,
                concentration: 0.5,
                water: 0.8,
                softness: 0.3,
                span: StrokeSpan::FULL,
            }),
        );
        Scene {
            id: SceneId("test".into()),
            size_hint: SizeHint {
                width: 256,
                height: 256,
            },
            paper: Paper::cold_press(seed),
            palette: Palette::moonlight(),
            timeline,
            seed,
            sim_resolution: SimResolution(128),
            background: Background::Transparent,
        }
    }

    #[test]
    fn isotropic_scale_keeps_the_short_side_unit() {
        assert_eq!(isotropic_scale(1.0), (1.0, 1.0));
        assert_eq!(isotropic_scale(4.0), (4.0, 1.0));
        assert_eq!(isotropic_scale(0.25), (1.0, 4.0));
        assert_eq!(isotropic_scale(f32::NAN), (1.0, 1.0));
        let mut s = valid_scene();
        s.size_hint = SizeHint {
            width: 1024,
            height: 256,
        };
        assert_eq!(s.aspect(), 4.0);
    }

    #[test]
    fn valid_scene_passes() {
        assert_eq!(valid_scene().validate(), Ok(()));
    }

    #[test]
    fn reports_every_error() {
        let mut s = valid_scene();
        s.sim_resolution = SimResolution(1024);
        s.timeline.events.clear();
        s.timeline.total_ticks = 0;
        let errs = s.validate().unwrap_err();
        assert!(errs.contains(&ValidationError::SimResolutionOutOfRange {
            value: 1024,
            min: 64,
            max: 512
        }));
        assert!(errs.contains(&ValidationError::EmptyTimeline));
        assert!(errs.contains(&ValidationError::ZeroTotalTicks));
    }

    #[test]
    fn rejects_nan_and_bad_pigment_index() {
        let mut s = valid_scene();
        if let Operation::Brush(b) = &mut s.timeline.events[0].op {
            b.concentration = f32::NAN;
            b.pigment = 99;
            b.path[0].x = 1.5;
        }
        let errs = s.validate().unwrap_err();
        assert!(errs.contains(&ValidationError::NotFinite {
            event: Some(0),
            field: "concentration"
        }));
        assert!(errs.contains(&ValidationError::PigmentIndexOutOfRange {
            event: 0,
            index: 99,
            count: 4
        }));
        assert!(errs.contains(&ValidationError::PointOutOfRange { event: 0, point: 0 }));
    }
}
