//! Health icons: heart, blood drop, heart rate, shield, and a group of
//! people.
//!
//! Two of these are drawn rather than filled. A heart-rate trace and a group
//! of outlined figures are lines, not bodies, so they are laid as strokes —
//! a stencil would give them a solid interior they should not have.

use std::f32::consts::{FRAC_PI_2, PI, TAU};

use nocturne_watercolour_core::domain::{Palette, Paper, PigmentRole, Scene};

use super::geometry::Frame;
use super::{Painting, SQUARE, Shape, Style, brush, role, stencil_body, tapered, water};

/// Two lobes above a point. The cusp between the lobes has to stay open or
/// the shape reads as a circle.
pub(super) fn heart(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let accent = role(palette, PigmentRole::Accent);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape::sampled(72, |t| {
        let a = t * TAU;
        // The classic parametric heart, scaled into the frame.
        let x = 16.0 * a.sin().powi(3);
        let y = 13.0 * a.cos() - 5.0 * (2.0 * a).cos() - 2.0 * (3.0 * a).cos() - (4.0 * a).cos();
        (0.5 + x / 42.0, 0.52 - y / 42.0)
    });
    stencil_body(&mut p, &frame, style, &body, 0.012, accent, 0.5, 0.0);
    if style.fine() {
        p.at(
            0.22,
            brush(
                frame.line(0.36, 0.38, 0.32, 0.5),
                0.05,
                shadow,
                style.conc(0.25),
                style.water(0.5),
                0.9,
            ),
        );
    }
    p.glaze(0.6, 0.1).clear_mask(0.6);
    if style.full() {
        p.at(
            0.66,
            water(vec![frame.pt(0.58, 0.42)], 0.07, style.water(0.55), 0.8),
        );
    }
    p.settle(0.82, 3.0);
    style.scene(
        "heart",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A round bottom drawn up into a point: a drop hangs, so the weight sits low.
pub(super) fn blood_drop(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let accent = role(palette, PigmentRole::Accent);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    // A point at the top and a round belly below: the arc is swept from one
    // shoulder round the bottom to the other, and closed back to the tip.
    // Pinching a circle instead leaves a cleft where the tip should be, which
    // is a heart, not a drop.
    let (cx, cy, r) = (0.5f32, 0.62f32, 0.25f32);
    let (from, sweep) = (-0.7f32, PI + 1.4);
    let body = Shape(
        std::iter::once((cx, 0.17))
            .chain((0..=40).map(|i| {
                let a = from + i as f32 / 40.0 * sweep;
                (cx + r * a.cos(), cy + r * a.sin())
            }))
            .collect(),
    );
    stencil_body(&mut p, &frame, style, &body, 0.012, accent, 0.52, 0.0);
    p.glaze(0.6, 0.1).clear_mask(0.6);
    if style.fine() {
        p.at(
            0.64,
            brush(
                vec![frame.pt(0.42, 0.68)],
                0.045,
                shadow,
                style.conc(0.22),
                style.water(0.4),
                0.9,
            ),
        );
    }
    p.settle(0.82, 3.0);
    style.scene(
        "blood-drop",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A trace, not a body: a flat baseline, one tall spike, a dip, and out. The
/// spike is drawn last and hardest so the eye lands on it.
pub(super) fn heart_rate(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let accent = role(palette, PigmentRole::Accent);
    let base = role(palette, PigmentRole::BaseWash);
    let thickness = if style.fine() { 0.036 } else { 0.05 };
    let mut p = Painting::new(style.ticks(320));
    let lead: Vec<_> = [(0.08f32, 0.5f32), (0.3, 0.5), (0.37, 0.42)]
        .iter()
        .map(|(x, y)| frame.pt(*x, *y))
        .collect();
    let tail: Vec<_> = [(0.63f32, 0.58f32), (0.7, 0.5), (0.92, 0.5)]
        .iter()
        .map(|(x, y)| frame.pt(*x, *y))
        .collect();
    p.at(
        0.0,
        brush(
            lead,
            thickness,
            base,
            style.conc(0.7),
            style.water(0.7),
            0.4,
        ),
    );
    p.at(
        0.3,
        brush(
            tail,
            thickness,
            base,
            style.conc(0.7),
            style.water(0.7),
            0.4,
        ),
    );
    let spike: Vec<_> = [(0.37f32, 0.42f32), (0.45, 0.16), (0.54, 0.78), (0.63, 0.58)]
        .iter()
        .map(|(x, y)| frame.pt(*x, *y))
        .collect();
    p.at(
        0.45,
        tapered(
            spike,
            (thickness, thickness * 0.8),
            accent,
            style.conc(1.1),
            style.water(0.5),
            0.3,
        ),
    );
    if style.full() {
        p.at(
            0.72,
            water(vec![frame.pt(0.5, 0.46)], 0.09, style.water(0.5), 0.8),
        );
    }
    p.settle(0.8, 3.0);
    style.scene(
        "heart-rate",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Square shoulders drawn down to a point.
pub(super) fn shield(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape(
        std::iter::once((0.2f32, 0.2f32))
            .chain(std::iter::once((0.8, 0.2)))
            .chain(std::iter::once((0.8, 0.48)))
            .chain((0..=14).map(|i| {
                let t = i as f32 / 14.0;
                // Both flanks sweep in to the point at the bottom.
                let x = 0.8 - 0.3 * t;
                (x, 0.48 + 0.4 * (t * FRAC_PI_2).sin())
            }))
            .chain((0..=14).map(|i| {
                let t = i as f32 / 14.0;
                let x = 0.5 - 0.3 * t;
                (x, 0.88 - 0.4 * (t * FRAC_PI_2).sin())
            }))
            .collect(),
    );
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.46, 0.0);
    p.glaze(0.58, 0.1).clear_mask(0.58);
    if style.fine() {
        p.at(
            0.6,
            brush(
                vec![
                    frame.pt(0.38, 0.46),
                    frame.pt(0.47, 0.57),
                    frame.pt(0.64, 0.36),
                ],
                0.032,
                shadow,
                style.conc(0.85),
                style.water(0.3),
                0.45,
            ),
        );
    }
    p.settle(0.84, 3.0);
    style.scene(
        "shield",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Three figures in outline only: a ring for each head and an arc for each
/// pair of shoulders, the middle one in front.
pub(super) fn people_group(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let accent = role(palette, PigmentRole::Accent);
    let stroke = if style.fine() { 0.034 } else { 0.048 };
    let mut p = Painting::new(style.ticks(380));
    // At Small the two behind are heads alone: at that size their shoulders
    // close up against the front figure's and the group reads as one mass.
    let figure = |at: f32,
                  cx: f32,
                  cy: f32,
                  r: f32,
                  span: f32,
                  pigment: usize,
                  shoulders: bool,
                  p: &mut Painting| {
        p.at(
            at,
            brush(
                frame.ring(cx, cy, r, 28),
                stroke,
                pigment,
                style.conc(0.85),
                style.water(0.7),
                0.35,
            ),
        );
        if !shoulders {
            return;
        }
        let arc: Vec<_> = (0..=20)
            .map(|i| {
                let t = i as f32 / 20.0;
                let a = PI + t * PI;
                // `a` runs across the upper half, where sine is negative, so
                // adding it lifts the arc toward the head. The centre sits far
                // enough below that the arc's crown clears the chin instead of
                // cutting across the face.
                frame.pt(cx + span * a.cos(), cy + r * 2.5 + span * 0.8 * a.sin())
            })
            .collect();
        p.at(
            at + 0.06,
            brush(
                arc,
                stroke,
                pigment,
                style.conc(0.85),
                style.water(0.7),
                0.35,
            ),
        );
    };
    figure(0.0, 0.26, 0.38, 0.105, 0.17, base, style.fine(), &mut p);
    figure(0.18, 0.74, 0.38, 0.105, 0.17, base, style.fine(), &mut p);
    p.glaze(0.42, 0.1);
    figure(0.46, 0.5, 0.33, 0.125, 0.2, accent, true, &mut p);
    p.settle(0.84, 3.0);
    style.scene(
        "people-group",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
