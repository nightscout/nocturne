//! Scenes: wide artworks built from stencilled washes laid wet-on-dry, so
//! each shape keeps a crisp edge and overlaps darken optically, with the
//! wet-into-wet passes (reflections, drop-ins) kept to where softness is the
//! point.

use nocturne_watercolour_core::application::Reveal;
use nocturne_watercolour_core::domain::{
    Background, Palette, Paper, PigmentRole, Point, Scene, SceneId, Seed, SimResolution, SizeHint,
};

use super::geometry::{Crescent, Frame, Hills, Notch, WaterBody};
use super::{
    DEFAULT_INTENSITY, DetailLevel, Painting, SQUARE, Style, brush, choreograph_scene,
    granulating_role, lift, role, tapered, water,
};

pub(super) const WIDE_16_9: SizeHint = SizeHint {
    width: 512,
    height: 288,
};

pub(super) const WIDE_2_1: SizeHint = SizeHint {
    width: 512,
    height: 256,
};

/// A loose rectangular wash of the base pigment with the accent dropped in
/// while wet, on cold-press paper so granulation and the dried edge read.
pub fn wash(seed: Seed, palette: Palette) -> Scene {
    let base = role(&palette, PigmentRole::BaseWash);
    let accent = role(&palette, PigmentRole::Accent);
    let shadow = role(&palette, PigmentRole::Shadow);
    let mut reveal = Reveal::new(320);
    // Uneven water per row: the brush is wetter at the start of a wash and
    // drier toward the end, so the sheet is never a flat slab of water.
    let rows = [
        (0.34, 1.35, 0.32),
        (0.44, 1.1, 0.36),
        (0.54, 0.95, 0.4),
        (0.64, 0.8, 0.42),
    ];
    for (i, (y, water, conc)) in rows.iter().enumerate() {
        let wobble = if i % 2 == 0 { 0.012 } else { -0.012 };
        reveal = reveal.wash(brush(
            vec![
                Point::new(0.22, y + wobble),
                Point::new(0.5, y - wobble * 0.5),
                Point::new(0.78, y + wobble),
            ],
            0.075,
            base,
            *conc,
            *water,
            0.25,
        ));
    }
    reveal = reveal
        .drop_in(
            0.06,
            brush(
                vec![Point::new(0.62, 0.42), Point::new(0.68, 0.5)],
                0.06,
                accent,
                0.9,
                0.35,
                0.6,
            ),
        )
        .drop_in(
            0.12,
            brush(vec![Point::new(0.34, 0.6)], 0.045, shadow, 0.7, 0.25, 0.6),
        )
        .settle(0.55, 3.5);
    Scene {
        id: SceneId(format!("wash-{}-{}", palette.name, seed.0)),
        size_hint: SQUARE,
        paper: Paper::cold_press(seed),
        palette,
        timeline: reveal.build(),
        seed,
        sim_resolution: SimResolution(256),
        background: Background::Transparent,
    }
}

/// `(centre_x, centre_y, radius, role)` of one soft rounded shape.
type Shape = (f32, f32, f32, PigmentRole);

const THREE_SHAPES: [Shape; 3] = [
    (0.37, 0.43, 0.2, PigmentRole::BaseWash),
    (0.61, 0.5, 0.19, PigmentRole::Shadow),
    (0.47, 0.65, 0.18, PigmentRole::Accent),
];

/// Two overlapping rounded shapes in two pigments, the second laid
/// wet-on-dry over the first so the overlap mixes optically and the second
/// edge stays crisp.
pub fn glaze_pair(seed: Seed, palette: Palette) -> Scene {
    let style = Style::new(seed, DEFAULT_INTENSITY, DetailLevel::Large);
    let mut scene = shapes_scene("glaze-pair", &style, &palette, &THREE_SHAPES[..2]);
    choreograph_scene(&mut scene, &style.choreography());
    scene
}

/// Three overlapping rounded shapes suggesting connection: each is glazed
/// wet-on-dry over the last, so every overlap darkens behind a crisp edge.
pub(super) fn overlapping_shapes(style: &Style, palette: &Palette) -> Scene {
    shapes_scene("overlapping-shapes", style, palette, &THREE_SHAPES)
}

fn shapes_scene(id: &str, style: &Style, palette: &Palette, shapes: &[Shape]) -> Scene {
    let frame = Frame::new(SQUARE);
    let mut p = Painting::new(style.ticks(160 + 160 * shapes.len() as u32));
    let phase = 0.9 / shapes.len() as f32;
    for (i, &(cx, cy, r, which)) in shapes.iter().enumerate() {
        let pigment = role(palette, which);
        let f = i as f32 * phase;
        if i > 0 {
            p.glaze(f, 0.1);
        }
        let rows = if style.fine() { 9 } else { 5 };
        let radius = frame.hatch_radius(cy - r, cy + r, rows);
        // The stencil owns the silhouette; the body only has to deliver pigment
        // inside it. Hatch the pigment in so the reveal has a path to pace;
        // the turns sit outside the mask.
        p.at(
            f,
            brush(
                frame.hatch(cx - r - radius, cx + r + radius, cy - r, cy + r, rows),
                radius,
                pigment,
                style.conc(0.55 * 0.9),
                style.water(0.6),
                0.85,
            ),
        );
        if style.fine() {
            p.at(
                f + phase * 0.15,
                brush(
                    vec![frame.pt(cx - r * 0.2, cy - r * 0.2)],
                    r * 0.3,
                    pigment,
                    style.conc(0.6),
                    style.water(0.3),
                    0.8,
                ),
            );
        }
        if style.full() {
            p.at(
                f + phase * 0.3,
                water(
                    vec![frame.pt(cx + r * 0.35, cy + r * 0.3)],
                    r * 0.3,
                    style.water(0.6),
                    0.7,
                ),
            );
        }
    }
    p.settle(0.92, 3.0);
    style.scene(
        id,
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Indigo water below a horizon, its wash stencilled around a gap that a
/// gold glaze later fills as the moon's reflection; a warm granulating spit
/// of shore along the horizon and the crescent above.
pub(super) fn moonlit_shoreline(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_16_9);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let accent = role(palette, PigmentRole::Accent);
    let glow = role(palette, PigmentRole::Glow);
    let shore = granulating_role(palette, &[PigmentRole::Shadow, PigmentRole::Accent]);
    let (moon_x, moon_y, moon_r) = (1.32, 0.2, 0.075);
    let body = WaterBody {
        x0: 0.05,
        x1: frame.aspect - 0.06,
        top: 0.52,
        bottom: 0.96,
        seed: style.seed(),
    };
    let notch = Notch {
        x: moon_x,
        top: body.top,
        bottom: 0.88,
        hw: [0.09, 0.03],
        seed: style.seed(),
    };
    let mut p = Painting::new(style.ticks(520));
    let water_poly = if style.fine() {
        body.with_notch(&frame, &notch)
    } else {
        body.polygon(&frame)
    };
    p.mask(0.0, water_poly, 0.012);
    let rows = if style.fine() { 8 } else { 5 };
    let radius = frame.hatch_radius(body.top, body.bottom, rows);
    // The stencil owns the silhouette; the body only has to deliver pigment
    // inside it. Pre-wet the footprint, then hatch the pigment in so the
    // reveal has a path to pace. The turns sit outside the mask.
    p.at(
        0.0,
        water(
            frame.line(0.1, 0.7, frame.aspect - 0.12, 0.7),
            0.22,
            style.water(0.75),
            0.15,
        ),
    );
    p.at(
        0.0,
        brush(
            frame.hatch(0.0, frame.aspect, body.top, body.bottom, rows),
            radius,
            base,
            style.conc(0.42),
            style.water(0.42),
            0.75,
        ),
    );
    if style.fine() {
        p.at(
            0.05,
            brush(
                vec![frame.pt(0.35, 0.62), frame.pt(0.6, 0.64)],
                0.06,
                shadow,
                style.conc(0.6),
                style.water(0.3),
                0.9,
            ),
        );
        p.at(
            0.08,
            brush(
                vec![frame.pt(1.5, 0.66)],
                0.05,
                shadow,
                style.conc(0.5),
                style.water(0.3),
                0.9,
            ),
        );
    }
    // A damp brush along the bottom edge, twice while still wet, softens the
    // rim the drying boundary would otherwise leave, so the wash dissolves
    // into the page instead of stopping at an outline.
    p.settle(0.12, 2.5);
    for f in [0.18, 0.3] {
        p.at(f, lift(body.floor_path(&frame, 0.02), 0.07, 0.7, 0.9));
    }
    if style.full() {
        for &(x0, x1, y) in &[(0.2, 0.7, 0.575), (0.45, 0.95, 0.6), (0.9, 1.15, 0.585)] {
            p.at(
                0.12,
                brush(
                    frame.line(x0, y, x1, y),
                    0.012,
                    shadow,
                    style.conc(0.7),
                    style.water(0.2),
                    0.5,
                ),
            );
        }
    }
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        tapered(
            vec![
                frame.pt(0.0, 0.5),
                frame.pt(0.5, 0.505),
                frame.pt(1.05, 0.515),
            ],
            (0.06, 0.014),
            shore,
            style.conc(1.2),
            style.water(0.5),
            0.4,
        ),
    );
    if style.fine() {
        p.at(
            0.5,
            brush(
                vec![frame.pt(0.12, 0.465), frame.pt(0.32, 0.475)],
                0.045,
                shore,
                style.conc(1.0),
                style.water(0.4),
                0.5,
            ),
        );
        p.at(
            0.52,
            brush(
                vec![frame.pt(0.2, 0.49), frame.pt(0.6, 0.5)],
                0.02,
                accent,
                style.conc(0.35),
                style.water(0.2),
                0.9,
            ),
        );
        p.at(
            0.53,
            brush(
                frame.line(0.0, 0.525, 0.55, 0.53),
                0.012,
                shadow,
                style.conc(0.4),
                style.water(0.15),
                0.9,
            ),
        );
        p.glaze(0.72, 0.1);
        p.mask(0.72, notch.wedge(&frame), 0.01);
        p.at(
            0.72,
            brush(
                frame.line(moon_x, 0.54, moon_x, 0.86),
                0.03,
                glow,
                style.conc(0.55),
                style.water(0.5),
                0.9,
            ),
        );
    }
    p.glaze(0.8, 0.1);
    if style.fine() {
        let crescent = Crescent::at(moon_x, moon_y, moon_r, 0.4);
        p.mask(0.8, frame.map(&crescent.mask_outline(0.02, 64)), 0.004);
        let spine = frame.map(&crescent.spine(7));
        let mid = spine.len() / 2;
        let body = |path: Vec<Point>, radius: (f32, f32)| {
            let (conc, wet) = style.glow(1.3, 0.7);
            tapered(path, radius, glow, conc, wet, 0.3)
        };
        p.at(0.8, body(spine[..=mid].to_vec(), (0.02, 0.045)));
        p.at(0.8, body(spine[mid..].to_vec(), (0.045, 0.02)));
        if !style.dark() {
            let concave = frame.map(&crescent.concave_edge(0.4, 7));
            p.at(
                0.84,
                brush(
                    concave[2..=4].to_vec(),
                    0.012,
                    shadow,
                    style.conc(0.4),
                    style.water(0.2),
                    0.9,
                ),
            );
        }
    } else {
        p.mask(0.8, frame.circle(moon_x, moon_y, moon_r, 24), 0.005);
        p.at(
            0.8,
            brush(
                vec![frame.pt(moon_x, moon_y)],
                0.12,
                glow,
                style.glow(1.4, 0.6).0,
                style.glow(1.4, 0.6).1,
                0.2,
            ),
        );
    }
    p.settle(0.92, 3.0);
    style.scene(
        "moonlit-shoreline",
        palette,
        WIDE_16_9,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Ridges receding in three translucent glazes laid wet-on-dry, each edge
/// crisp and every overlap darker, mirrored below the waterline as softer
/// wet-into-wet shapes.
pub(super) fn distant_mountains(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_2_1);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let glow = role(palette, PigmentRole::Glow);
    let base_y = 0.62;
    let mut stream = style.stream(1);
    let mut seeded = || Seed(stream.next_u64());
    let far = Hills {
        base_y,
        bumps: vec![(0.5, 0.45, 0.22), (1.3, 0.5, 0.3), (1.85, 0.35, 0.18)],
        wobble: 0.02,
        seed: seeded(),
    };
    let mid = Hills {
        base_y,
        bumps: vec![(0.25, 0.3, 0.14), (0.95, 0.35, 0.17), (1.6, 0.4, 0.13)],
        wobble: 0.02,
        seed: seeded(),
    };
    let near = Hills {
        base_y,
        bumps: vec![(0.7, 0.35, 0.09), (1.4, 0.45, 0.11)],
        wobble: 0.015,
        seed: seeded(),
    };
    let ridges: Vec<(&Hills, usize, f32, usize)> = if style.fine() {
        vec![
            // The far ridge is the first thing the pen draws and carries most
            // of the picture, so it gets the finer hatch: a longer path keeps
            // the wash arriving instead of arriving whole.
            (&far, base, 0.4, 12),
            (&mid, shadow, 0.4, 7),
            (&near, shadow, 0.55, 7),
        ]
    } else {
        vec![(&far, base, 0.5, 5), (&near, shadow, 0.6, 5)]
    };
    let mut p = Painting::new(style.ticks(560));
    let fill = |pigment: usize, conc: f32, rows: usize| {
        let radius = frame.hatch_radius(0.1, base_y, rows);
        // The stencil owns the silhouette; the body only has to deliver
        // pigment inside it. Pre-wet the footprint, then hatch the pigment
        // in so the reveal has a path to pace. The turns sit outside the mask.
        (
            water(
                frame.line(0.0, base_y - 0.2, 2.0, base_y - 0.2),
                0.4,
                style.water(0.75),
                0.15,
            ),
            brush(
                frame.hatch(0.0, 2.0, 0.1, base_y, rows),
                radius,
                pigment,
                style.conc(conc * 0.6),
                style.water(0.42),
                0.75,
            ),
        )
    };
    let phase = 0.66 / ridges.len() as f32;
    for (i, &(hills, pigment, conc, rows)) in ridges.iter().enumerate() {
        let f = i as f32 * phase;
        if i > 0 {
            p.dry(f);
        }
        p.mask(
            f,
            frame.ridge(0.0, 2.0, base_y, 48, |x| hills.height(x)),
            0.008,
        );
        let (wet, hatched) = fill(pigment, conc, rows);
        p.at(f, wet);
        p.at(f, hatched);
        if style.fine() {
            p.at(
                f + phase * 0.15,
                brush(
                    frame.line(0.1, base_y - 0.04, 1.9, base_y - 0.04),
                    0.03,
                    pigment,
                    style.conc(0.3),
                    style.water(0.25),
                    1.0,
                ),
            );
        }
    }
    let reflect = 0.68;
    let squash = 0.6;
    p.dry(reflect);
    // Thin, low-water fills so the mirrored shapes settle where they land
    // rather than flowing to a rim; a little clean water at the waterline
    // lets them bleed into each other there. The final mask is the union of
    // the reflections, widely feathered, so the bleed fades into dry paper.
    let union = frame.ridge(0.0, 2.0, base_y, 48, |x| {
        ridges
            .iter()
            .map(|(h, _, _, _)| h.reflected_height(x, squash))
            .fold(base_y, f32::max)
    });
    p.mask(reflect, union.clone(), 0.1);
    p.at(
        reflect,
        water(
            frame.line(0.0, base_y + 0.02, 2.0, base_y + 0.02),
            0.05,
            style.water(0.7),
            0.8,
        ),
    );
    for &(hills, pigment, conc, _) in &ridges {
        p.mask(
            reflect,
            frame.ridge(0.0, 2.0, base_y, 48, |x| hills.reflected_height(x, squash)),
            0.04,
        );
        // Centred on the waterline with a wide soft falloff, so the water
        // thins before the mask edge and the mirror fades instead of rimming.
        p.at(
            reflect,
            brush(
                frame.line(0.0, base_y + 0.01, 2.0, base_y + 0.01),
                0.2,
                pigment,
                style.conc(conc * 0.35),
                style.water(0.25),
                0.95,
            ),
        );
    }
    p.mask(reflect, union, 0.1);
    let mirror_floor: Vec<Point> = (0..=24)
        .map(|i| {
            let x = 2.0 * i as f32 / 24.0;
            let y = ridges
                .iter()
                .map(|(h, _, _, _)| h.reflected_height(x, squash))
                .fold(base_y, f32::max);
            frame.pt(x, y - 0.015)
        })
        .collect();
    p.settle(reflect + 0.05, 2.5);
    for f in [0.08, 0.16] {
        p.at(reflect + f, lift(mirror_floor.clone(), 0.04, 0.7, 0.9));
    }
    if style.fine() {
        // Lifted bands break the mirror into strips of still water.
        let gaps: &[(f32, f32, f32)] = if style.full() {
            &[(0.1, 1.9, 0.045), (0.3, 1.7, 0.1), (0.6, 1.5, 0.15)]
        } else {
            &[(0.1, 1.9, 0.05), (0.4, 1.6, 0.11)]
        };
        for &(xa, xb, dy) in gaps {
            p.at(
                reflect + 0.04,
                lift(
                    frame.line(xa, base_y + dy, xb, base_y + dy + 0.004),
                    0.009,
                    0.85,
                    0.4,
                ),
            );
        }
    }
    if style.full() {
        let moon = 0.9;
        p.dry(moon);
        p.mask(moon, frame.circle(1.55, 0.2, 0.06, 24), 0.004);
        p.at(
            moon,
            brush(
                vec![frame.pt(1.55, 0.2)],
                0.1,
                glow,
                style.glow(1.4, 0.6).0,
                style.glow(1.4, 0.6).1,
                0.2,
            ),
        );
    }
    p.settle(0.94, 3.0);
    style.scene(
        "distant-mountains",
        palette,
        WIDE_2_1,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Two warm shores facing each other across indigo water, joined by a gap
/// in the wash that a gold glaze fills as a path of reflected light.
pub(super) fn connected_shores(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(WIDE_2_1);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let accent = role(palette, PigmentRole::Accent);
    let glow = role(palette, PigmentRole::Glow);
    let top = 0.38;
    let body = WaterBody {
        x0: 0.0,
        x1: 2.0,
        top,
        bottom: 0.97,
        seed: style.seed(),
    };
    // The light path is a wedge open at the left edge (under the left shore)
    // narrowing to a tip under the right one, so the water stays one simple
    // polygon.
    let slit_lower = [(0.5, 0.555), (1.0, 0.535), (1.3, 0.52)];
    let slit_upper = [(1.3, 0.49), (1.0, 0.475), (0.5, 0.46)];
    let slit_tip = Point::new(1.56, 0.505);
    let mut slit = vec![frame.pt(0.0, 0.57)];
    slit.extend(slit_lower.iter().map(|&(x, y)| frame.pt(x, y)));
    slit.push(frame.pt(slit_tip.x, slit_tip.y));
    slit.extend(slit_upper.iter().map(|&(x, y)| frame.pt(x, y)));
    slit.push(frame.pt(0.0, 0.45));
    let mut water_poly = vec![frame.pt(0.0, top), frame.pt(2.0, top)];
    water_poly.extend(body.below(&frame, 0.57));
    water_poly.extend(slit[1..].iter().copied());
    let left_shore = frame.map(&[
        Point::new(0.0, 0.3),
        Point::new(0.2, 0.31),
        Point::new(0.4, 0.36),
        Point::new(0.55, 0.45),
        Point::new(0.5, 0.53),
        Point::new(0.3, 0.58),
        Point::new(0.0, 0.6),
    ]);
    let right_shore = frame.map(&[
        Point::new(2.0, 0.34),
        Point::new(1.75, 0.35),
        Point::new(1.55, 0.42),
        Point::new(1.47, 0.5),
        Point::new(1.55, 0.57),
        Point::new(1.8, 0.6),
        Point::new(2.0, 0.62),
    ]);
    let mut p = Painting::new(style.ticks(560));
    p.mask(0.0, water_poly, 0.012);
    let rows = if style.fine() { 8 } else { 5 };
    let radius = frame.hatch_radius(body.top, body.bottom, rows);
    // The stencil owns the silhouette; the body only has to deliver pigment
    // inside it. Pre-wet the footprint, then hatch the pigment in so the
    // reveal has a path to pace. The turns sit outside the mask.
    p.at(
        0.0,
        water(
            frame.line(0.0, 0.65, 2.0, 0.65),
            0.28,
            style.water(0.75),
            0.15,
        ),
    );
    p.at(
        0.0,
        brush(
            frame.hatch(0.0, 2.0, body.top, body.bottom, rows),
            radius,
            base,
            style.conc(0.42),
            style.water(0.42),
            0.75,
        ),
    );
    if style.fine() {
        p.at(
            0.05,
            brush(
                vec![frame.pt(0.9, 0.7), frame.pt(1.2, 0.72)],
                0.07,
                shadow,
                style.conc(0.55),
                style.water(0.3),
                0.9,
            ),
        );
        p.at(
            0.08,
            brush(
                vec![frame.pt(0.75, 0.42)],
                0.05,
                shadow,
                style.conc(0.5),
                style.water(0.25),
                0.9,
            ),
        );
    }
    let shore =
        |p: &mut Painting, f: f32, poly: Vec<Point>, x0: f32, x1: f32, waterline: (f32, f32)| {
            p.glaze(f, 0.1);
            p.mask(f, poly, 0.01);
            let rows = if style.fine() { 6 } else { 4 };
            let radius = frame.hatch_radius(0.3, 0.6, rows);
            // The stencil owns the silhouette; the body only has to deliver pigment
            // inside it. Pre-wet the footprint, then hatch the pigment in so the
            // reveal has a path to pace. The turns sit outside the mask.
            p.at(
                f,
                water(
                    frame.line(x0 + 0.08, 0.45, x1 - 0.08, 0.45),
                    0.3,
                    style.water(0.75),
                    0.15,
                ),
            );
            p.at(
                f,
                brush(
                    frame.hatch(x0, x1, 0.3, 0.6, rows),
                    radius,
                    accent,
                    style.conc(0.8 * 0.6),
                    style.water(0.42),
                    0.75,
                ),
            );
            if style.fine() {
                p.at(
                    f + 0.04,
                    brush(
                        frame.line(waterline.0, 0.55, waterline.1, 0.56),
                        0.03,
                        shadow,
                        style.conc(0.5),
                        style.water(0.2),
                        0.9,
                    ),
                );
            }
        };
    p.settle(0.1, 2.5);
    for f in [0.16, 0.28] {
        p.at(f, lift(body.floor_path(&frame, 0.02), 0.07, 0.7, 0.9));
    }
    shore(&mut p, 0.42, left_shore, 0.0, 0.62, (0.05, 0.4));
    shore(&mut p, 0.6, right_shore, 1.4, 2.0, (1.6, 1.95));
    p.glaze(0.78, 0.1);
    p.mask(0.78, slit, 0.01);
    p.at(
        0.78,
        brush(
            vec![
                frame.pt(0.45, 0.51),
                frame.pt(1.0, 0.505),
                frame.pt(1.5, 0.505),
            ],
            0.035,
            glow,
            style.conc(0.6),
            style.water(0.5),
            0.8,
        ),
    );
    if style.full() {
        p.glaze(0.9, 0.1);
        p.mask(0.9, frame.circle(1.0, 0.16, 0.05, 24), 0.004);
        p.at(
            0.9,
            brush(
                vec![frame.pt(1.0, 0.16)],
                0.09,
                glow,
                style.glow(1.4, 0.6).0,
                style.glow(1.4, 0.6).1,
                0.2,
            ),
        );
    }
    p.settle(0.94, 3.0);
    style.scene(
        "connected-shores",
        palette,
        WIDE_2_1,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
