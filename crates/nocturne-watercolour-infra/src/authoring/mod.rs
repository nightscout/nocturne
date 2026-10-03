//! Artwork catalogue: named scene constructors. Every artwork is a function
//! of a seed, a palette, an intensity and a [`DetailLevel`]; the ids in
//! [`ArtworkCatalogue::IDS`] match the TypeScript union the web package
//! exposes.

mod accents;
mod geometry;
mod icons;
mod icons_objects;
mod icons_places;
mod icons_time;
mod icons_vitals;
mod scenes;
mod svg;

pub use nocturne_watercolour_core::application::Choreography;
use nocturne_watercolour_core::application::{
    apply_settle_fraction, choreograph, settle_after_last_stroke, settle_share_for_ticks,
};
use nocturne_watercolour_core::domain::scene::{MAX_CONCENTRATION, MAX_WATER};
use nocturne_watercolour_core::domain::seed::SeedStream;
use nocturne_watercolour_core::domain::{
    Background, BrushStroke, LiftStroke, Mask, Operation, Palette, Paper, PigmentRole, Point,
    RadiusProfile, Scene, SceneId, Seed, SimResolution, SizeHint, StrokeSpan, SubSeed, Timeline,
    WaterStroke,
};

use geometry::Frame;
pub use scenes::{glaze_pair, wash};
pub use svg::{
    FLATTEN_TOLERANCE, IconHints, IconNode, SubPath, parse_icon_elements, parse_icon_hints,
    svg_icon_scene,
};

pub type ArtworkFn = fn(Seed, Palette) -> Scene;

pub struct ArtworkEntry {
    pub name: &'static str,
    pub build: ArtworkFn,
}

/// How much of an artwork to draw. Chosen from the size it will be shown at:
/// a 32-48 px icon cannot carry secondary strokes, and a 96-192 px one only
/// some of them. The simulation grid scales with it so small accents are
/// cheap; fewer ticks suffice on a coarser grid because water covers more of
/// the sheet per tick. `ExtraLarge` is the tier for a large backing store
/// (the web package derives it from the canvas's DPR-scaled long edge).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DetailLevel {
    Small,
    Medium,
    Large,
    ExtraLarge,
}

impl DetailLevel {
    pub const ALL: [DetailLevel; 4] = [
        DetailLevel::Small,
        DetailLevel::Medium,
        DetailLevel::Large,
        DetailLevel::ExtraLarge,
    ];

    pub fn sim_resolution(self) -> u32 {
        match self {
            DetailLevel::Small => 96,
            DetailLevel::Medium => 160,
            DetailLevel::Large => 256,
            DetailLevel::ExtraLarge => 384,
        }
    }

    fn tick_scale(self) -> f32 {
        match self {
            DetailLevel::Small => 0.5,
            DetailLevel::Medium => 0.75,
            DetailLevel::Large => 1.0,
            DetailLevel::ExtraLarge => 1.25,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            DetailLevel::Small => "small",
            DetailLevel::Medium => "medium",
            DetailLevel::Large => "large",
            DetailLevel::ExtraLarge => "extralarge",
        }
    }
}

pub struct ArtworkCatalogue;

impl ArtworkCatalogue {
    /// Every artwork the web package can ask for, hero icons first, then
    /// scenes, then accents.
    pub const IDS: &'static [&'static str] = &[
        "crescent-moon",
        "alarm-bell",
        "linked-rings",
        "report-pages",
        "magnifying-glass",
        "confirmation-mark",
        "moonlit-shoreline",
        "distant-mountains",
        "connected-shores",
        "overlapping-shapes",
        "avatar-wash",
        "tab-underline",
        "selection-edge",
        "confirmation-background",
        "header-motif",
        "calendar",
        "clock",
        "stopwatch",
        "sunrise",
        "footprints",
        "apple",
        "pizza-slice",
        "spanner",
        "suitcase",
        "paint-palette",
        "key",
        "plug",
        "apartment",
        "world-globe",
        "github-mark",
        "heart",
        "blood-drop",
        "heart-rate",
        "shield",
        "people-group",
        "exclamation-mark",
        "chat-bubble",
        "phone",
    ];

    pub fn ids() -> &'static [&'static str] {
        Self::IDS
    }

    /// `by_id_for` on a light ground.
    pub fn by_id(
        id: &str,
        seed: Seed,
        palette: &Palette,
        intensity: f32,
        detail: DetailLevel,
    ) -> Option<Scene> {
        Self::by_id_for(
            id,
            seed,
            palette,
            intensity,
            detail,
            Background::Transparent,
        )
    }

    /// `intensity` (`0..1`, `0.7` is the designed look) scales pigment
    /// concentration and, more gently, the water each stroke lays down, so
    /// higher values also pool and rim harder. `background` is both stored on
    /// the scene and consulted while authoring: under
    /// `Background::TransparentOnDark` (luminous compositing) a shadow glaze
    /// inside a glow shape greys it and a tint over clear paper reads as a
    /// slab, so those marks are left out.
    pub fn by_id_for(
        id: &str,
        seed: Seed,
        palette: &Palette,
        intensity: f32,
        detail: DetailLevel,
        background: Background,
    ) -> Option<Scene> {
        Self::by_id_for_with_resolution(id, seed, palette, intensity, detail, background, None)
    }

    /// [`by_id_for`] with the simulation resolution overridden instead of the
    /// detail's default, so a large display can run a finer grid than the
    /// `ExtraLarge` 384 without re-authoring the scene. `sim_resolution` is
    /// clamped to `SimResolution::MIN..=SimResolution::MAX`; `None` keeps the
    /// detail default.
    pub fn by_id_for_with_resolution(
        id: &str,
        seed: Seed,
        palette: &Palette,
        intensity: f32,
        detail: DetailLevel,
        background: Background,
        sim_resolution: Option<u32>,
    ) -> Option<Scene> {
        let mut scene = Self::by_id_for_unchoreographed(
            id,
            seed,
            palette,
            intensity,
            detail,
            background,
            sim_resolution,
        )?;
        let style = style_for(seed, intensity, detail, background, sim_resolution);
        choreograph_scene(&mut scene, &style.choreography());
        Some(scene)
    }

    /// [`by_id_for`] with the choreography left off, so a caller can apply its
    /// own timing (see [`choreograph_scene_with`]) instead of the detail's
    /// defaults. `sim_resolution` is as [`Self::by_id_for_with_resolution`].
    pub fn by_id_for_unchoreographed(
        id: &str,
        seed: Seed,
        palette: &Palette,
        intensity: f32,
        detail: DetailLevel,
        background: Background,
        sim_resolution: Option<u32>,
    ) -> Option<Scene> {
        let style = style_for(seed, intensity, detail, background, sim_resolution);
        let scene = match id {
            "crescent-moon" => icons::crescent_moon(&style, palette),
            "alarm-bell" => icons::alarm_bell(&style, palette),
            "linked-rings" => icons::linked_rings(&style, palette),
            "report-pages" => icons::report_pages(&style, palette),
            "magnifying-glass" => icons::magnifying_glass(&style, palette),
            "confirmation-mark" => icons::confirmation_mark(&style, palette),
            "moonlit-shoreline" => scenes::moonlit_shoreline(&style, palette),
            "distant-mountains" => scenes::distant_mountains(&style, palette),
            "connected-shores" => scenes::connected_shores(&style, palette),
            "overlapping-shapes" => scenes::overlapping_shapes(&style, palette),
            "avatar-wash" => accents::avatar_wash(&style, palette),
            "tab-underline" => accents::tab_underline(&style, palette),
            "selection-edge" => accents::selection_edge(&style, palette),
            "confirmation-background" => accents::confirmation_background(&style, palette),
            "header-motif" => accents::header_motif(&style, palette),
            "calendar" => icons_time::calendar(&style, palette),
            "clock" => icons_time::clock(&style, palette),
            "stopwatch" => icons_time::stopwatch(&style, palette),
            "sunrise" => icons_time::sunrise(&style, palette),
            "footprints" => icons_time::footprints(&style, palette),
            "apple" => icons_objects::apple(&style, palette),
            "pizza-slice" => icons_objects::pizza_slice(&style, palette),
            "spanner" => icons_objects::spanner(&style, palette),
            "suitcase" => icons_objects::suitcase(&style, palette),
            "paint-palette" => icons_objects::paint_palette(&style, palette),
            "key" => icons_places::key(&style, palette),
            "plug" => icons_places::plug(&style, palette),
            "apartment" => icons_places::apartment(&style, palette),
            "world-globe" => icons_places::world_globe(&style, palette),
            "github-mark" => icons_places::github_mark(&style, palette),
            "heart" => icons_vitals::heart(&style, palette),
            "blood-drop" => icons_vitals::blood_drop(&style, palette),
            "heart-rate" => icons_vitals::heart_rate(&style, palette),
            "shield" => icons_vitals::shield(&style, palette),
            "people-group" => icons_vitals::people_group(&style, palette),
            "exclamation-mark" => icons_places::exclamation_mark(&style, palette),
            "chat-bubble" => icons_places::chat_bubble(&style, palette),
            "phone" => icons_places::phone(&style, palette),
            _ => return None,
        };
        Some(scene)
    }

    /// The original three constructors, kept for the callers that address
    /// artworks by underscore name; `by_id` is the catalogue proper.
    pub fn entries() -> Vec<ArtworkEntry> {
        vec![
            ArtworkEntry {
                name: "wash",
                build: wash,
            },
            ArtworkEntry {
                name: "crescent_moon",
                build: crescent_moon,
            },
            ArtworkEntry {
                name: "glaze_pair",
                build: glaze_pair,
            },
        ]
    }

    pub fn build(name: &str, seed: Seed, palette: Palette) -> Option<Scene> {
        Self::entries()
            .into_iter()
            .find(|e| e.name == name)
            .map(|e| (e.build)(seed, palette))
    }
}

/// `crescent-moon` at the designed intensity and full detail.
pub fn crescent_moon(seed: Seed, palette: Palette) -> Scene {
    let style = Style::new(seed, DEFAULT_INTENSITY, DetailLevel::Large);
    let mut scene = icons::crescent_moon(&style, &palette);
    choreograph_scene(&mut scene, &style.choreography());
    scene
}

pub const DEFAULT_INTENSITY: f32 = 0.7;

/// Ticks of a reveal spent settling, for a caller that wants a fixed tail
/// rather than the catalogue's own (which settles from the last stroke on,
/// so the whole tail is the sheet setting into the page).
pub const SETTLE_TICK_FRACTION: f32 = 0.45;

/// Per-build knobs every artwork reads its amounts through.
#[derive(Debug, Clone, Copy)]
pub(crate) struct Style {
    seed: Seed,
    intensity: f32,
    detail: DetailLevel,
    background: Background,
    /// Simulation grid override; `None` uses the detail default.
    resolution: Option<SimResolution>,
}

impl Style {
    pub fn new(seed: Seed, intensity: f32, detail: DetailLevel) -> Style {
        Style {
            seed,
            intensity: if intensity.is_finite() {
                intensity.clamp(0.0, 1.0)
            } else {
                DEFAULT_INTENSITY
            },
            detail,
            background: Background::Transparent,
            resolution: None,
        }
    }

    pub fn on(mut self, background: Background) -> Style {
        self.background = background;
        self
    }

    pub fn with_resolution(mut self, resolution: Option<SimResolution>) -> Style {
        self.resolution = resolution;
        self
    }

    pub fn seed(&self) -> Seed {
        self.seed
    }

    /// The artwork will be shown on a dark ground with luminous compositing.
    pub fn dark(&self) -> bool {
        self.background == Background::TransparentOnDark
    }

    /// Concentration for a stroke designed at `base` for intensity 0.7:
    /// 0.3 keeps two thirds of it, 1.0 adds a quarter.
    pub fn conc(&self, base: f32) -> f32 {
        (base * (0.4 + self.intensity * 0.857)).clamp(0.0, MAX_CONCENTRATION)
    }

    pub fn water(&self, base: f32) -> f32 {
        (base * (0.8 + self.intensity * 0.3)).clamp(0.0, MAX_WATER)
    }

    /// `(concentration, water)` for a glow-only shape such as a moon. Under
    /// luminous compositing the dips in a thin deposit let the dark ground
    /// through as grey clouding, so on a dark ground the shape is laid
    /// heavier and drier to keep the deposit high everywhere.
    pub fn glow(&self, conc: f32, water: f32) -> (f32, f32) {
        if self.dark() {
            (self.conc(conc * 1.5), self.water(water * 0.55))
        } else {
            (self.conc(conc), self.water(water))
        }
    }

    pub fn ticks(&self, large: u32) -> u32 {
        ((large as f32 * self.detail.tick_scale()).round() as u32).max(16)
    }

    /// Secondary strokes are drawn at Medium and above.
    pub fn fine(&self) -> bool {
        self.detail != DetailLevel::Small
    }

    /// Tertiary detail is drawn at Large and above.
    pub fn full(&self) -> bool {
        matches!(self.detail, DetailLevel::Large | DetailLevel::ExtraLarge)
    }

    /// An independent random stream for geometry decisions; `purpose` keeps
    /// two uses in one artwork from sharing a sequence.
    pub fn stream(&self, purpose: u32) -> SeedStream {
        self.seed
            .derive(SubSeed::Brush(0x4155_0000 | purpose))
            .stream()
    }

    pub fn resolution(&self) -> SimResolution {
        self.resolution
            .unwrap_or(SimResolution(self.detail.sim_resolution()))
    }

    /// The choreography a reveal for this detail level is drawn with. All the
    /// timing fields are window-relative shares or per-window sizes, so the
    /// detail level's tick scale changes nothing.
    pub fn choreography(&self) -> Choreography {
        Choreography::default()
    }

    pub fn scene(
        &self,
        id: &str,
        palette: &Palette,
        size: SizeHint,
        paper: Paper,
        timeline: Timeline,
    ) -> Scene {
        Scene {
            id: SceneId(format!("{id}-{}-{}", palette.name, self.seed.0)),
            size_hint: size,
            paper,
            palette: palette.clone(),
            timeline,
            seed: self.seed,
            sim_resolution: self.resolution(),
            background: self.background,
        }
    }
}

/// A [`Style`] for the catalogue arguments, shared by the scene constructors
/// and the choreography call so both see the same detail and resolution.
pub(crate) fn style_for(
    seed: Seed,
    intensity: f32,
    detail: DetailLevel,
    background: Background,
    sim_resolution: Option<u32>,
) -> Style {
    let resolution =
        sim_resolution.map(|res| SimResolution(res.clamp(SimResolution::MIN, SimResolution::MAX)));
    Style::new(seed, intensity, detail)
        .on(background)
        .with_resolution(resolution)
}

/// Timeline builder with explicit phases. `Reveal` handles one mask and one
/// wet-on-dry glaze pass; the catalogue's scenes stencil several shapes in
/// sequence, each dried before the next, so they schedule events directly.
pub(crate) struct Painting {
    timeline: Timeline,
    total: u32,
}

impl Painting {
    pub fn new(total: u32) -> Painting {
        let total = total.max(2);
        Painting {
            timeline: Timeline::new(total),
            total,
        }
    }

    fn tick(&self, f: f32) -> u32 {
        ((f.clamp(0.0, 1.0) * self.total as f32).round() as u32).min(self.total - 1)
    }

    pub fn at(&mut self, f: f32, op: Operation) -> &mut Self {
        self.timeline.push(self.tick(f), op);
        self
    }

    pub fn mask(&mut self, f: f32, points: Vec<Point>, feather: f32) -> &mut Self {
        self.at(f, Operation::SetMask(Mask::Polygon { points, feather }))
    }

    pub fn clear_mask(&mut self, f: f32) -> &mut Self {
        self.at(f, Operation::ClearMask)
    }

    /// Starts a wet-on-dry phase: everything so far is settled and the
    /// evaporation rate returns to base.
    ///
    /// `DryAll` is instant, which costs the reveal its tail: every pigment
    /// still in suspension is deposited on that one tick, so an artwork that
    /// glazes has nothing left to settle afterwards and holds a finished frame
    /// for the rest of its run. Prefer [`Painting::glaze`] unless the next
    /// mark really must land on a bone-dry sheet.
    pub fn dry(&mut self, f: f32) -> &mut Self {
        self.at(f, Operation::DryAll);
        self.at(f, Operation::Dry { rate: 1.0 })
    }

    /// A glaze boundary that dries over a window rather than in one tick:
    /// the wash below is taken down by `Operation::Settle`, which removes a
    /// share of the film each tick, so its pigment deposits gradually and
    /// keeps working through the tail. Returns to the base rate at the end of
    /// the window, by which point the sheet is dry enough to take a crisp
    /// wet-on-dry mark.
    ///
    /// The sheet is dry *at* `f`, having been taken down over the `over`
    /// before it, so a mark laid at `f` still lands wet-on-dry. `over` is a
    /// share of the whole timeline; a tenth is enough for the deepest film
    /// the simulation allows.
    pub fn glaze(&mut self, f: f32, over: f32) -> &mut Self {
        let window = ((over.clamp(0.01, 1.0) * self.total as f32).round() as u32).max(2);
        let span = window as f32 / self.total as f32;
        self.at(
            (f - span).max(0.0),
            Operation::Settle {
                share: settle_share_for_ticks(window),
            },
        );
        self.at(f, Operation::Settle { share: 0.0 });
        self.at(f, Operation::Dry { rate: 1.0 })
    }

    pub fn settle(&mut self, f: f32, rate: f32) -> &mut Self {
        self.at(f, Operation::Dry { rate })
    }

    pub fn finish(mut self) -> Timeline {
        self.timeline.push(self.total, Operation::DryAll);
        self.timeline
    }
}

/// A closed outline in design space: the silhouette an icon is recognised by.
///
/// Icons in this catalogue are stencil-and-fill drawings. The `SetMask` built
/// from this outline is what carries the shape; the pigment behind it only has
/// to arrive everywhere inside. Keeping the outline in design space (`y` over
/// `0..1`, `x` over `0..aspect`) lets [`Shape::bounds`] size the hatch that
/// fills it without the caller doing the arithmetic twice.
#[derive(Debug, Clone)]
struct Shape(Vec<(f32, f32)>);

impl Shape {
    /// A closed polygon sampled from a parametric edge, `t` running `0..1`.
    fn sampled(samples: usize, edge: impl Fn(f32) -> (f32, f32)) -> Shape {
        Shape(
            (0..samples.max(3))
                .map(|i| edge(i as f32 / samples.max(3) as f32))
                .collect(),
        )
    }

    fn circle(cx: f32, cy: f32, r: f32, samples: usize) -> Shape {
        Shape::sampled(samples, |t| {
            let a = t * std::f32::consts::TAU;
            (cx + r * a.cos(), cy + r * a.sin())
        })
    }

    /// A rectangle with its corners rounded by `r`.
    fn rounded_rect(x0: f32, y0: f32, x1: f32, y1: f32, r: f32) -> Shape {
        let r = r
            .min((x1 - x0).abs() * 0.5)
            .min((y1 - y0).abs() * 0.5)
            .max(0.0);
        let corner = |cx: f32, cy: f32, from: f32| {
            (0..=6).map(move |i| {
                let a = from + i as f32 / 6.0 * std::f32::consts::FRAC_PI_2;
                (cx + r * a.cos(), cy + r * a.sin())
            })
        };
        let half_pi = std::f32::consts::FRAC_PI_2;
        let mut pts: Vec<(f32, f32)> = Vec::with_capacity(28);
        pts.extend(corner(x1 - r, y0 + r, -half_pi));
        pts.extend(corner(x1 - r, y1 - r, 0.0));
        pts.extend(corner(x0 + r, y1 - r, half_pi));
        pts.extend(corner(x0 + r, y0 + r, 2.0 * half_pi));
        Shape(pts)
    }

    /// `(x0, x1, y0, y1)` in design space.
    fn bounds(&self) -> (f32, f32, f32, f32) {
        self.0.iter().fold(
            (f32::MAX, f32::MIN, f32::MAX, f32::MIN),
            |(x0, x1, y0, y1), (x, y)| (x0.min(*x), x1.max(*x), y0.min(*y), y1.max(*y)),
        )
    }

    fn polygon(&self, frame: &Frame) -> Vec<Point> {
        self.0.iter().map(|(x, y)| frame.pt(*x, *y)).collect()
    }
}

/// Rows a stencilled body is hatched with at each detail level. Enough that
/// the row pitch is well under the narrowest part of any icon silhouette, and
/// fewer at `Small` where there are fewer ticks to spend.
fn hatch_rows(style: &Style, band: f32) -> usize {
    let target = if style.fine() { 0.085 } else { 0.13 };
    ((band / target).round() as usize).clamp(5, 11)
}

/// Lays an icon body inside its own stencil, the way a flat wash is laid.
///
/// One wide stamp would deliver the pigment on the reveal's first step and
/// there would be nothing to watch arrive, and a narrower brush would leave
/// the stencil unfilled and the object unrecognisable. So the footprint is
/// pre-wet in a single pass — water alone leaves no mark — and the pigment is
/// hatched across it in overlapping sweeps, which is both how a wash is
/// actually laid and a path long enough for the pen to pace along. The turns
/// sit outside the mask and are clipped away.
///
/// Leaves the mask set: the caller adds marks that should be clipped to the
/// silhouette, then `dry` and `clear_mask` before anything that should not.
#[allow(clippy::too_many_arguments)]
fn stencil_body(
    p: &mut Painting,
    frame: &Frame,
    style: &Style,
    shape: &Shape,
    feather: f32,
    pigment: usize,
    conc: f32,
    at: f32,
) {
    let (x0, x1, y0, y1) = shape.bounds();
    let rows = hatch_rows(style, y1 - y0);
    let pitch = (y1 - y0) / (rows.max(2) - 1) as f32;
    // Inset the first and last row by half a pitch so the hatch's own soft
    // edge lands inside the stencil rather than being clipped in half.
    let (top, bottom) = (y0 + pitch * 0.25, y1 - pitch * 0.25);
    let (cx, cy) = ((x0 + x1) * 0.5, (y0 + y1) * 0.5);
    let reach = ((x1 - x0).max(y1 - y0)) * 0.55;
    p.mask(at, shape.polygon(frame), feather);
    p.at(
        at,
        water(
            frame.line(cx, cy - reach * 0.2, cx, cy + reach * 0.2),
            reach,
            style.water(0.75),
            0.15,
        ),
    );
    p.at(
        at,
        brush(
            frame.hatch(x0 - 0.12, x1 + 0.12, top, bottom, rows),
            frame.hatch_radius(top, bottom, rows),
            pigment,
            style.conc(conc),
            style.water(0.42),
            0.75,
        ),
    );
}

pub(crate) fn role(palette: &Palette, role: PigmentRole) -> usize {
    palette.index_of(role).unwrap_or(0)
}

/// Of `candidates`, the role whose pigment granulates most (a shore or a
/// rock wants texture, whatever hue the palette gives that role); the first
/// candidate when none is present.
pub(crate) fn granulating_role(palette: &Palette, candidates: &[PigmentRole]) -> usize {
    candidates
        .iter()
        .filter_map(|&r| palette.index_of(r))
        .max_by(|&a, &b| {
            let g = |i: usize| palette.pigment(i).map_or(0.0, |p| p.granulation);
            g(a).total_cmp(&g(b))
        })
        .unwrap_or_else(|| role(palette, candidates[0]))
}

pub(crate) fn brush(
    path: Vec<Point>,
    radius: f32,
    pigment: usize,
    concentration: f32,
    water: f32,
    softness: f32,
) -> Operation {
    Operation::Brush(BrushStroke {
        path,
        radius: RadiusProfile::uniform(radius),
        pigment,
        concentration,
        water,
        softness,
        span: StrokeSpan::FULL,
    })
}

pub(crate) fn tapered(
    path: Vec<Point>,
    radius: (f32, f32),
    pigment: usize,
    concentration: f32,
    water: f32,
    softness: f32,
) -> Operation {
    Operation::Brush(BrushStroke {
        path,
        radius: RadiusProfile {
            start: radius.0,
            end: radius.1,
        },
        pigment,
        concentration,
        water,
        softness,
        span: StrokeSpan::FULL,
    })
}

/// A damp brush drawn through wet paint: removes pigment and water under it.
pub(crate) fn lift(path: Vec<Point>, radius: f32, strength: f32, softness: f32) -> Operation {
    Operation::Lift(LiftStroke {
        path,
        radius: RadiusProfile::uniform(radius),
        strength,
        softness,
        span: StrokeSpan::FULL,
    })
}

pub(crate) fn water(path: Vec<Point>, radius: f32, amount: f32, softness: f32) -> Operation {
    Operation::Water(WaterStroke {
        path,
        radius: RadiusProfile::uniform(radius),
        water: amount,
        softness,
        span: StrokeSpan::FULL,
    })
}

/// Rewrites a built scene's timeline so its strokes are drawn (see
/// [`choreograph`]), settling over the given fraction of the ticks. A
/// `settle_fraction` of zero settles from the last stroke instead, which is
/// what [`choreograph_scene`] does; the fraction is for callers measuring or
/// tuning the reveal against a fixed tail.
pub fn choreograph_scene_with(scene: &mut Scene, params: &Choreography, settle_fraction: f32) {
    scene.timeline = choreograph(&scene.timeline, params);
    if settle_fraction > 0.0 {
        apply_settle_fraction(&mut scene.timeline, settle_fraction);
    } else {
        settle_after_last_stroke(&mut scene.timeline);
    }
}

/// Rewrites a built scene's timeline so its strokes are drawn (see
/// [`choreograph`]) and settles from the last stroke on
/// (`settle_after_last_stroke`). Every scene-producing entry point runs this
/// on the finished scene just before it is returned.
pub(crate) fn choreograph_scene(scene: &mut Scene, params: &Choreography) {
    choreograph_scene_with(scene, params, 0.0);
}

pub(crate) const SQUARE: SizeHint = SizeHint {
    width: 512,
    height: 512,
};

#[cfg(test)]
mod tests {
    use super::*;
    use geometry::{Crescent, dist};

    #[test]
    fn every_legacy_entry_validates_for_every_palette() {
        for entry in ArtworkCatalogue::entries() {
            for name in Palette::NAMES {
                let scene = (entry.build)(Seed(1), Palette::by_name(name).unwrap());
                assert_eq!(scene.validate(), Ok(()), "{} / {name}", entry.name);
            }
        }
    }

    #[test]
    fn intensity_scales_amounts_within_bounds() {
        let lo = Style::new(Seed(1), 0.0, DetailLevel::Large);
        let hi = Style::new(Seed(1), 1.0, DetailLevel::Large);
        assert!(lo.conc(1.0) > 0.3 && lo.conc(1.0) < 0.5);
        assert!(hi.conc(1.0) > 1.2 && hi.conc(1.0) < 1.3);
        assert!(hi.conc(10.0) <= MAX_CONCENTRATION);
        assert!(hi.water(10.0) <= MAX_WATER);
        let nan = Style::new(Seed(1), f32::NAN, DetailLevel::Large);
        assert_eq!(nan.intensity, DEFAULT_INTENSITY);
    }

    #[test]
    fn painting_phases_land_in_order_and_end_dry() {
        let mut p = Painting::new(100);
        p.mask(0.0, vec![Point::new(0.0, 0.0)], 0.01)
            .dry(0.5)
            .at(0.5, Operation::ClearMask)
            .settle(0.8, 3.0);
        let t = p.finish();
        let ticks: Vec<u32> = t.events.iter().map(|e| e.at_tick).collect();
        assert_eq!(ticks, vec![0, 50, 50, 50, 80, 100]);
        assert_eq!(t.events[1].op, Operation::DryAll);
        assert_eq!(t.events[3].op, Operation::ClearMask);
        assert_eq!(t.events.last().unwrap().op, Operation::DryAll);
    }

    #[test]
    fn crescent_outline_is_a_closed_ring_of_two_arcs() {
        let c = Crescent::at(0.5, 0.5, 0.27, 0.5);
        let outline = c.outline(96);
        assert!(outline.len() > 20 && outline.len() < 192);
        let spine = c.spine(9);
        assert_eq!(spine.len(), 9);
        let mask = c.mask_outline(0.09, 96);
        assert_eq!(mask.len(), outline.len());
        assert!(mask.iter().any(|p| dist(*p, c.centre) > c.radius + 0.08));
        for p in c.concave_edge(0.3, 7) {
            assert!(dist(p, c.inner_centre) >= c.inner_radius - 1e-4);
        }
    }

    #[test]
    fn every_catalogue_id_validates_after_choreography_for_every_detail_and_ground() {
        let palette = Palette::moonlight();
        for id in ArtworkCatalogue::ids() {
            for detail in DetailLevel::ALL {
                for background in [Background::Transparent, Background::TransparentOnDark] {
                    let scene =
                        ArtworkCatalogue::by_id_for(id, Seed(7), &palette, 0.7, detail, background)
                            .unwrap_or_else(|| {
                                panic!("{id} / {detail:?} / {background:?} missing")
                            });
                    assert_eq!(
                        scene.validate(),
                        Ok(()),
                        "{id} / {detail:?} / {background:?}"
                    );
                }
            }
        }
    }
}
