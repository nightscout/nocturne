//! Painting operations. Geometry is in normalised scene coordinates
//! (`0..1` on both axes) so a scene is independent of simulation and output
//! resolution.

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Point {
    pub x: f32,
    pub y: f32,
}

impl Point {
    pub const fn new(x: f32, y: f32) -> Self {
        Point { x, y }
    }
}

/// Brush radius interpolated linearly from the first path point to the last,
/// in normalised units.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RadiusProfile {
    pub start: f32,
    pub end: f32,
}

impl RadiusProfile {
    pub const fn uniform(r: f32) -> Self {
        RadiusProfile { start: r, end: r }
    }

    pub fn at(&self, t: f32) -> f32 {
        self.start + (self.end - self.start) * t.clamp(0.0, 1.0)
    }

    pub fn max(&self) -> f32 {
        self.start.max(self.end)
    }
}

/// The part of a stroke's path a single application lays down, as arc-length
/// fractions of the whole path. Spans of one stroke tile `0..1`; each cell
/// receives paint from the first span that reaches it, so the union of a
/// stroke's spans deposits exactly what `FULL` would.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StrokeSpan {
    pub start: f32,
    pub end: f32,
}

impl StrokeSpan {
    pub const FULL: StrokeSpan = StrokeSpan {
        start: 0.0,
        end: 1.0,
    };

    /// Clamps both ends to `0..=1` and orders them so `start <= end`.
    pub const fn new(start: f32, end: f32) -> Self {
        let start = start.clamp(0.0, 1.0);
        let end = end.clamp(0.0, 1.0);
        if start <= end {
            StrokeSpan { start, end }
        } else {
            StrokeSpan {
                start: end,
                end: start,
            }
        }
    }

    pub fn is_full(&self) -> bool {
        self.start <= 0.0 && self.end >= 1.0
    }

    pub fn is_empty(&self) -> bool {
        self.end <= self.start
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct BrushStroke {
    pub path: Vec<Point>,
    pub radius: RadiusProfile,
    /// Index into the scene palette.
    pub pigment: usize,
    /// Pigment concentration added per fully covered cell.
    pub concentration: f32,
    /// Water depth added per fully covered cell.
    pub water: f32,
    /// `0` is a hard-edged stamp, `1` fades from the centre line.
    pub softness: f32,
    pub span: StrokeSpan,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Dab {
    pub center: Point,
    pub radius: f32,
    pub pigment: usize,
    pub concentration: f32,
    pub water: f32,
    pub softness: f32,
}

impl Dab {
    pub fn as_brush(&self) -> BrushStroke {
        BrushStroke {
            path: vec![self.center],
            radius: RadiusProfile::uniform(self.radius),
            pigment: self.pigment,
            concentration: self.concentration,
            water: self.water,
            softness: self.softness,
            span: StrokeSpan::FULL,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct WaterStroke {
    pub path: Vec<Point>,
    pub radius: RadiusProfile,
    pub water: f32,
    pub softness: f32,
    pub span: StrokeSpan,
}

/// Removes pigment (suspended and deposited) and water under the stroke.
#[derive(Debug, Clone, PartialEq)]
pub struct LiftStroke {
    pub path: Vec<Point>,
    pub radius: RadiusProfile,
    /// Fraction removed per fully covered cell.
    pub strength: f32,
    pub softness: f32,
    pub span: StrokeSpan,
}

/// Region paint may occupy. Inside the shape the mask is `1`; outside it falls
/// to `0` over `feather` (normalised units), and water beyond the mask
/// evaporates in proportion, so `feather` controls how far a wash may bleed
/// past its intended edge.
#[derive(Debug, Clone, PartialEq)]
pub enum Mask {
    Polygon {
        points: Vec<Point>,
        feather: f32,
    },
    Path {
        points: Vec<Point>,
        radius: f32,
        feather: f32,
    },
}

impl Mask {
    pub fn points(&self) -> &[Point] {
        match self {
            Mask::Polygon { points, .. } | Mask::Path { points, .. } => points,
        }
    }

    pub fn feather(&self) -> f32 {
        match self {
            Mask::Polygon { feather, .. } | Mask::Path { feather, .. } => *feather,
        }
    }
}

/// Largest share of the remaining film one tick of a settle may take. A
/// share of one would empty every wet cell in a single tick, which is a
/// `DryAll` by another name.
pub const MAX_SETTLE_SHARE: f32 = 0.5;

#[derive(Debug, Clone, PartialEq)]
pub enum Operation {
    Brush(BrushStroke),
    Dab(Dab),
    Water(WaterStroke),
    Lift(LiftStroke),
    /// Scales evaporation from this point on; `1.0` is the base rate.
    Dry {
        rate: f32,
    },
    /// Dries the sheet in proportion to the film still on it: each tick takes
    /// `share` of every cell's remaining depth, on top of whatever
    /// [`Operation::Dry`] is set to. `0` turns it off.
    ///
    /// A reveal has to arrive at dry on the tick its timeline ends, because
    /// the implicit `DryAll` there settles whatever is left in one step and
    /// that reads as a jump. A fixed rate cannot do it: the time a constant
    /// evaporation needs is proportional to the water on the sheet, and a
    /// full-canvas wash carries twenty times what an icon does. Taking a
    /// share instead makes the time logarithmic in the water, so one number
    /// lands every artwork within a few percent of its last tick.
    Settle {
        share: f32,
    },
    /// Instantly settles every suspended pigment and removes all water.
    DryAll,
    SetMask(Mask),
    ClearMask,
}

impl Operation {
    pub fn path(&self) -> Option<&[Point]> {
        match self {
            Operation::Brush(s) => Some(&s.path),
            Operation::Dab(s) => Some(std::slice::from_ref(&s.center)),
            Operation::Water(s) => Some(&s.path),
            Operation::Lift(s) => Some(&s.path),
            Operation::SetMask(m) => Some(m.points()),
            Operation::Dry { .. }
            | Operation::Settle { .. }
            | Operation::DryAll
            | Operation::ClearMask => None,
        }
    }
}
