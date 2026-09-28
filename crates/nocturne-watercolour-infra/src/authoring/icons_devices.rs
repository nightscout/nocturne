//! Diabetes devices: a CGM sensor, an insulin pump and a glucose gauge.

use std::f32::consts::{PI, TAU};

use nocturne_watercolour_core::domain::{Palette, Paper, PigmentRole, Scene};

use super::geometry::Frame;
use super::{Painting, SQUARE, Shape, Style, brush, lift, role, stencil_body, tapered};

/// A small disc on a wider adhesive patch, with the dimple at its centre. The
/// patch is what stops it reading as a button or a coin. The disc is laid
/// first, in its own stencil, because the round rim is the recognition: a
/// disc hatched into a patch that is still wet runs ragged. The patch then
/// goes on pale through a ring-shaped stencil that leaves the disc alone.
pub(super) fn cgm_sensor(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let (cx, cy, disc, patch) = (0.5f32, 0.5f32, 0.23f32, 0.37f32);
    let mut p = Painting::new(style.ticks(360));
    stencil_body(
        &mut p,
        &frame,
        style,
        &Shape::circle(cx, cy, disc, 48),
        0.01,
        base,
        0.6,
        0.0,
    );
    if style.full() {
        p.at(0.1, lift(frame.line(0.41, 0.4, 0.37, 0.5), 0.03, 0.5, 0.8));
    }
    p.glaze(0.4, 0.1).clear_mask(0.4);
    // Even-odd fill: the outer circle, a bridge in, the inner circle the
    // other way round, and the bridge back out, so the disc is a hole.
    let n = 56;
    let ring = |r: f32, i: usize| {
        let a = i as f32 / n as f32 * TAU;
        frame.pt(cx + r * a.cos(), cy + r * a.sin())
    };
    let annulus: Vec<_> = (0..=n)
        .map(|i| ring(patch, i))
        .chain((0..=n).rev().map(|i| ring(disc + 0.012, i)))
        .collect();
    let (top, bottom) = (cy - patch, cy + patch);
    let rows = if style.fine() { 15 } else { 9 };
    // Heavier and drier on a dark ground, where a thin tint clouds grey.
    let (conc, wet) = style.glow(0.1, 0.5);
    p.mask(0.42, annulus, 0.012);
    p.at(
        0.42,
        brush(
            frame.hatch(0.05, 0.95, top, bottom, rows),
            frame.hatch_radius(top, bottom, rows),
            base,
            conc,
            wet,
            0.9,
        ),
    );
    p.glaze(0.64, 0.1).clear_mask(0.64);
    if style.fine() {
        p.at(
            0.66,
            brush(
                frame.ring(cx, cy, 0.06, 20),
                0.02,
                shadow,
                style.conc(0.85),
                style.water(0.25),
                0.45,
            ),
        );
    } else {
        p.at(
            0.66,
            brush(
                vec![frame.pt(cx, cy)],
                0.06,
                shadow,
                style.conc(0.85),
                style.water(0.25),
                0.5,
            ),
        );
    }
    p.settle(0.84, 3.0);
    style.scene(
        "cgm-sensor",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A body wider than it is tall with a screen in it, and a tube that loops
/// up out of its top to the infusion site. The tube is what separates it
/// from a phone or a pager, so it is drawn at every size.
pub(super) fn insulin_pump(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let accent = role(palette, PigmentRole::Accent);
    let mut p = Painting::new(style.ticks(380));
    let body = Shape::rounded_rect(0.1, 0.44, 0.66, 0.86, 0.08);
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.4, 0.0);
    p.glaze(0.36, 0.09).clear_mask(0.36);
    let (top, bottom) = (0.53f32, 0.77);
    p.mask(
        0.38,
        Shape::rounded_rect(0.18, 0.52, 0.47, 0.78, 0.03).polygon(&frame),
        0.006,
    );
    p.at(
        0.38,
        brush(
            frame.hatch(0.1, 0.55, top, bottom, 5),
            frame.hatch_radius(top, bottom, 5),
            shadow,
            style.conc(1.4),
            style.water(0.18),
            0.7,
        ),
    );
    if style.fine() {
        p.at(
            0.56,
            lift(frame.line(0.23, 0.72, 0.33, 0.56), 0.025, 0.5, 0.6),
        );
    }
    p.glaze(0.62, 0.08).clear_mask(0.62);
    p.at(
        0.62,
        brush(
            vec![frame.pt(0.565, 0.65)],
            0.04,
            shadow,
            style.conc(0.85),
            style.water(0.25),
            0.45,
        ),
    );
    let (lx, rx, bend, rise) = (0.5f32, 0.84f32, 0.3f32, 0.15f32);
    let (mid, half) = ((lx + rx) * 0.5, (rx - lx) * 0.5);
    let tube: Vec<_> = std::iter::once((lx, 0.44))
        .chain((0..=16).map(|i| {
            let a = PI + i as f32 / 16.0 * PI;
            (mid + half * a.cos(), bend + rise * a.sin())
        }))
        .chain(std::iter::once((rx, 0.44)))
        .map(|(x, y)| frame.pt(x, y))
        .collect();
    p.at(
        0.66,
        brush(
            tube,
            if style.fine() { 0.018 } else { 0.03 },
            shadow,
            style.conc(0.8),
            style.water(0.28),
            0.4,
        ),
    );
    p.at(
        0.74,
        brush(
            vec![frame.pt(0.84, 0.5)],
            if style.fine() { 0.05 } else { 0.06 },
            accent,
            style.conc(0.8),
            style.water(0.3),
            0.5,
        ),
    );
    p.settle(0.84, 3.0);
    style.scene(
        "insulin-pump",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A half dial with ticks round its arc and a needle from the pivot: a
/// reading on a scale, for choosing the units glucose is shown in. The needle
/// is drawn last and darkest so the eye lands on it.
pub(super) fn glucose_gauge(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let accent = role(palette, PigmentRole::Accent);
    let (cx, cy, r) = (0.5f32, 0.68f32, 0.4f32);
    let mut p = Painting::new(style.ticks(360));
    let dial = Shape(
        (0..=40)
            .map(|i| {
                let a = PI + i as f32 / 40.0 * PI;
                (cx + r * a.cos(), cy + r * a.sin())
            })
            .chain([(cx + r, cy + 0.06), (cx - r, cy + 0.06)])
            .collect(),
    );
    stencil_body(&mut p, &frame, style, &dial, 0.012, base, 0.4, 0.0);
    p.glaze(0.5, 0.1).clear_mask(0.5);
    let ticks = if style.fine() { 5 } else { 3 };
    for i in 0..ticks {
        let a = PI + (i as f32 + 0.5) / ticks as f32 * PI;
        let (ux, uy) = (a.cos(), a.sin());
        p.at(
            0.52,
            brush(
                frame.line(
                    cx + ux * r * 0.66,
                    cy + uy * r * 0.66,
                    cx + ux * r * 0.84,
                    cy + uy * r * 0.84,
                ),
                if style.fine() { 0.02 } else { 0.03 },
                shadow,
                style.conc(0.6),
                style.water(0.22),
                0.5,
            ),
        );
    }
    let needle = PI + PI * 0.7;
    p.at(
        0.64,
        tapered(
            frame.line(
                cx,
                cy,
                cx + needle.cos() * r * 0.78,
                cy + needle.sin() * r * 0.78,
            ),
            (0.036, 0.014),
            accent,
            style.conc(1.0),
            style.water(0.3),
            0.4,
        ),
    );
    p.at(
        0.7,
        brush(
            vec![frame.pt(cx, cy)],
            0.05,
            shadow,
            style.conc(0.9),
            style.water(0.25),
            0.5,
        ),
    );
    p.settle(0.84, 3.0);
    style.scene(
        "glucose-gauge",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
