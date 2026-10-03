//! Time and place icons: calendar, clock, stopwatch, sunrise, footprints.
//!
//! Each body is a stencil filled by [`stencil_body`]; the marks that give the
//! object its meaning — hands, rings, rays — are laid wet-on-dry afterwards so
//! they keep a crisp edge against the wash.

use std::f32::consts::{PI, TAU};

use nocturne_watercolour_core::domain::{Palette, Paper, PigmentRole, Scene};

use super::geometry::Frame;
use super::{Painting, SQUARE, Shape, Style, brush, role, stencil_body};

/// A month block under a header band, with its two hanging rings.
pub(super) fn calendar(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape::rounded_rect(0.14, 0.24, 0.86, 0.88, 0.07);
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.44, 0.0);
    // The header band is laid inside the stencil so it stops at the body's
    // edge rather than overhanging it.
    p.at(
        0.1,
        brush(
            frame.line(0.1, 0.33, 0.9, 0.33),
            0.075,
            shadow,
            style.conc(0.5),
            style.water(0.3),
            0.6,
        ),
    );
    p.glaze(0.5, 0.1).clear_mask(0.5);
    for x in [0.34f32, 0.66] {
        p.at(
            0.5,
            brush(
                frame.line(x, 0.13, x, 0.27),
                0.028,
                shadow,
                style.conc(0.8),
                style.water(0.3),
                0.45,
            ),
        );
    }
    if style.fine() {
        for y in [0.52f32, 0.68] {
            for j in 0..3 {
                let x = 0.3 + j as f32 * 0.2;
                p.at(
                    0.55,
                    brush(
                        vec![frame.pt(x, y)],
                        0.028,
                        shadow,
                        style.conc(0.55),
                        style.water(0.25),
                        0.5,
                    ),
                );
            }
        }
    }
    p.settle(0.8, 3.0);
    style.scene(
        "calendar",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A round face with two hands; the hands are the whole of the reading, so
/// they are laid dry and dark against the wash.
pub(super) fn clock(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let (cx, cy, r) = (0.5f32, 0.52f32, 0.36f32);
    let mut p = Painting::new(style.ticks(340));
    stencil_body(
        &mut p,
        &frame,
        style,
        &Shape::circle(cx, cy, r, 48),
        0.012,
        base,
        0.44,
        0.0,
    );
    p.glaze(0.5, 0.1).clear_mask(0.5);
    let hand = |to: (f32, f32), radius: f32, conc: f32| {
        brush(
            vec![frame.pt(cx, cy), frame.pt(to.0, to.1)],
            radius,
            shadow,
            style.conc(conc),
            style.water(0.3),
            0.45,
        )
    };
    p.at(0.5, hand((cx, cy - r * 0.55), 0.028, 0.85));
    p.at(0.58, hand((cx + r * 0.62, cy + r * 0.2), 0.024, 0.8));
    if style.fine() {
        for i in 0..4 {
            let a = i as f32 / 4.0 * TAU;
            let (sx, sy) = (cx + a.cos() * r * 0.82, cy + a.sin() * r * 0.82);
            p.at(
                0.62,
                brush(
                    vec![frame.pt(sx, sy)],
                    0.022,
                    shadow,
                    style.conc(0.5),
                    style.water(0.2),
                    0.5,
                ),
            );
        }
    }
    p.settle(0.8, 3.0);
    style.scene(
        "clock",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A clock read as a stopwatch by its crown and side button, and by the hand
/// sitting where a running one does.
pub(super) fn stopwatch(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let accent = role(palette, PigmentRole::Accent);
    let shadow = role(palette, PigmentRole::Shadow);
    let (cx, cy, r) = (0.5f32, 0.6f32, 0.31f32);
    let mut p = Painting::new(style.ticks(340));
    stencil_body(
        &mut p,
        &frame,
        style,
        &Shape::circle(cx, cy, r, 48),
        0.012,
        base,
        0.44,
        0.0,
    );
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        brush(
            frame.line(0.42, 0.18, 0.58, 0.18),
            0.035,
            shadow,
            style.conc(0.8),
            style.water(0.3),
            0.4,
        ),
    );
    p.at(
        0.52,
        brush(
            frame.line(0.5, 0.18, 0.5, 0.3),
            0.03,
            shadow,
            style.conc(0.75),
            style.water(0.3),
            0.45,
        ),
    );
    p.at(
        0.54,
        brush(
            frame.line(0.72, 0.3, 0.8, 0.22),
            0.03,
            shadow,
            style.conc(0.7),
            style.water(0.3),
            0.45,
        ),
    );
    p.at(
        0.58,
        brush(
            vec![frame.pt(cx, cy), frame.pt(cx + r * 0.5, cy - r * 0.6)],
            0.028,
            accent,
            style.conc(0.9),
            style.water(0.3),
            0.45,
        ),
    );
    p.settle(0.8, 3.0);
    style.scene(
        "stopwatch",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A half disc on the horizon with rays above it; the horizon line is what
/// makes it a rising sun rather than a circle.
pub(super) fn sunrise(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let glow = role(palette, PigmentRole::Glow);
    let shadow = role(palette, PigmentRole::Shadow);
    let (cx, horizon, r) = (0.5f32, 0.68f32, 0.24f32);
    let mut p = Painting::new(style.ticks(340));
    let dome = Shape::sampled(40, |t| {
        let a = PI + t * PI;
        (cx + r * a.cos(), horizon + r * a.sin())
    });
    stencil_body(&mut p, &frame, style, &dome, 0.014, glow, 1.0, 0.0);
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        brush(
            frame.line(0.1, horizon, 0.9, horizon),
            0.026,
            shadow,
            style.conc(0.8),
            style.water(0.35),
            0.5,
        ),
    );
    if style.fine() {
        for i in 0..5 {
            let a = PI + (i as f32 + 0.5) / 5.0 * PI;
            let (inner, outer) = (r * 1.35, r * 1.75);
            p.at(
                0.56,
                brush(
                    vec![
                        frame.pt(cx + inner * a.cos(), horizon + inner * a.sin()),
                        frame.pt(cx + outer * a.cos(), horizon + outer * a.sin()),
                    ],
                    0.022,
                    glow,
                    style.conc(0.9),
                    style.water(0.25),
                    0.5,
                ),
            );
        }
    }
    p.settle(0.8, 3.0);
    style.scene(
        "sunrise",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// The width down a sole, from the ball to the heel: the outer edge runs
/// almost straight while the inner one is cut away at the arch. An ellipse
/// has neither, which is why two ellipses read as two ellipses.
const SOLE_V: [f32; 7] = [-1.0, -0.72, -0.35, 0.0, 0.42, 0.78, 1.0];
const SOLE_OUT: [f32; 7] = [0.25, 0.34, 0.33, 0.29, 0.26, 0.26, 0.13];
const SOLE_IN: [f32; 7] = [0.23, 0.31, 0.24, 0.11, 0.15, 0.22, 0.13];

/// One sole, `mirror`ed for the other foot and toed out by `tilt` radians.
/// The ball end is left blunt so the toes sit on it rather than above it.
fn sole(cx: f32, cy: f32, mirror: bool, tilt: f32) -> Shape {
    let (sx, sy) = (0.26f32, 0.17f32);
    let m = if mirror { -1.0 } else { 1.0 };
    let (c, s) = (tilt.cos(), tilt.sin());
    let place = |u: f32, v: f32| {
        let (x, y) = (u * m * sx, v * sy);
        (cx + x * c - y * s, cy + x * s + y * c)
    };
    let down = (0..7).map(|i| place(SOLE_OUT[i], SOLE_V[i]));
    let up = (0..7).rev().map(|i| place(-SOLE_IN[i], SOLE_V[i]));
    Shape(down.chain(up).collect())
}

/// Two soles walking away on a diagonal, each with a big toe and three
/// smaller ones.
pub(super) fn footprints(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let mut p = Painting::new(style.ticks(360));
    let feet = [
        (0.33f32, 0.35f32, false, -0.16f32),
        (0.66, 0.66, true, 0.16),
    ];
    stencil_body(
        &mut p,
        &frame,
        style,
        &sole(feet[0].0, feet[0].1, feet[0].2, feet[0].3),
        0.012,
        base,
        0.5,
        0.0,
    );
    p.glaze(0.4, 0.08).clear_mask(0.4);
    stencil_body(
        &mut p,
        &frame,
        style,
        &sole(feet[1].0, feet[1].1, feet[1].2, feet[1].3),
        0.012,
        base,
        0.5,
        0.44,
    );
    p.glaze(0.78, 0.08).clear_mask(0.78);
    // Toes, in the sole's own frame so they follow the tilt: the big one on
    // the inside edge, then three falling away across the ball.
    let toes: [(f32, f32, f32); 4] = [
        (-0.27, -1.14, 0.030),
        (-0.04, -1.21, 0.024),
        (0.14, -1.18, 0.022),
        (0.29, -1.09, 0.020),
    ];
    for (i, (cx, cy, mirror, tilt)) in feet.into_iter().enumerate() {
        let m = if mirror { -1.0 } else { 1.0 };
        let (c, s) = (tilt.cos(), tilt.sin());
        for (u, v, r) in toes {
            if !style.fine() && r < 0.024 {
                continue;
            }
            let (x, y) = (u * m * 0.26, v * 0.17);
            let at = frame.pt(cx + x * c - y * s, cy + x * s + y * c);
            p.at(
                0.8 + i as f32 * 0.02,
                brush(vec![at], r, base, style.conc(0.95), style.water(0.3), 0.45),
            );
        }
    }
    p.settle(0.9, 3.0);
    style.scene(
        "footprints",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
