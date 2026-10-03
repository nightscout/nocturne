//! Connection, place and signal icons: key, plug, apartment, world globe,
//! the GitHub mark, an exclamation, a chat bubble and a phone.
//!
//! The GitHub mark is drawn only as a schematic silhouette, for the places the
//! product names GitHub as a service. It is GitHub's trademark; see
//! `docs/watercolour/provenance-and-licences.md`.

use std::f32::consts::{PI, TAU};

use nocturne_watercolour_core::domain::{Palette, Paper, PigmentRole, Scene};

use super::geometry::Frame;
use super::{Painting, SQUARE, Shape, Style, brush, lift, role, stencil_body, tapered};

/// A bow at the top and a shaft with two teeth: the teeth are what stop it
/// reading as a balloon.
pub(super) fn key(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let (bx, by, br) = (0.36f32, 0.31f32, 0.17f32);
    let mut p = Painting::new(style.ticks(360));
    stencil_body(
        &mut p,
        &frame,
        style,
        &Shape::circle(bx, by, br, 40),
        0.012,
        base,
        0.5,
        0.0,
    );
    p.glaze(0.38, 0.1).clear_mask(0.38);
    // The bow's hole, lifted so the ring reads as a ring.
    p.at(0.4, lift(vec![frame.pt(bx, by)], 0.072, 0.95, 0.5));
    p.at(
        0.46,
        brush(
            frame.line(bx + br * 0.55, by + br * 0.55, 0.78, 0.78),
            0.05,
            base,
            style.conc(0.8),
            style.water(0.5),
            0.5,
        ),
    );
    for (t, len) in [(0.62f32, 0.1f32), (0.78, 0.075)] {
        let (x, y) = (0.36 + (0.78 - 0.36) * t, 0.31 + (0.78 - 0.31) * t);
        p.at(
            0.6,
            brush(
                frame.line(x, y, x + len, y - len),
                0.034,
                base,
                style.conc(0.85),
                style.water(0.4),
                0.5,
            ),
        );
    }
    if style.fine() {
        p.at(
            0.7,
            brush(
                frame.line(0.5, 0.48, 0.72, 0.7),
                0.018,
                shadow,
                style.conc(0.45),
                style.water(0.22),
                0.7,
            ),
        );
    }
    p.settle(0.84, 3.0);
    style.scene(
        "key",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A body with two pins above it and a lead below: read as a plug by the pair
/// of pins, so they are kept well separated.
pub(super) fn plug(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape::rounded_rect(0.28, 0.36, 0.72, 0.74, 0.1);
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.46, 0.0);
    p.glaze(0.48, 0.1).clear_mask(0.48);
    for x in [0.4f32, 0.6] {
        p.at(
            0.5,
            brush(
                frame.line(x, 0.17, x, 0.37),
                0.035,
                shadow,
                style.conc(0.85),
                style.water(0.3),
                0.45,
            ),
        );
    }
    p.at(
        0.58,
        brush(
            vec![
                frame.pt(0.5, 0.74),
                frame.pt(0.5, 0.86),
                frame.pt(0.62, 0.9),
            ],
            0.03,
            shadow,
            style.conc(0.75),
            style.water(0.3),
            0.5,
        ),
    );
    p.settle(0.82, 3.0);
    style.scene(
        "plug",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A block with a stepped top and a grid of windows.
pub(super) fn apartment(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(360));
    let block = Shape(vec![
        (0.22, 0.88),
        (0.22, 0.3),
        (0.5, 0.3),
        (0.5, 0.16),
        (0.78, 0.16),
        (0.78, 0.88),
    ]);
    stencil_body(&mut p, &frame, style, &block, 0.012, base, 0.44, 0.0);
    p.glaze(0.5, 0.1).clear_mask(0.5);
    if style.fine() {
        for row in 0..3 {
            for col in 0..3 {
                let x = 0.31 + col as f32 * 0.19;
                let y = 0.4 + row as f32 * 0.17;
                if x > 0.5 || y > 0.32 {
                    p.at(
                        0.52 + row as f32 * 0.03,
                        brush(
                            vec![frame.pt(x, y)],
                            0.034,
                            shadow,
                            style.conc(1.1),
                            style.water(0.2),
                            0.35,
                        ),
                    );
                }
            }
        }
    }
    p.at(
        0.66,
        brush(
            frame.line(0.44, 0.88, 0.44, 0.76),
            0.042,
            shadow,
            style.conc(1.1),
            style.water(0.25),
            0.4,
        ),
    );
    p.settle(0.84, 3.0);
    style.scene(
        "apartment",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A disc crossed by an equator and two meridians.
pub(super) fn world_globe(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let (cx, cy, r) = (0.5f32, 0.5f32, 0.36f32);
    let mut p = Painting::new(style.ticks(360));
    stencil_body(
        &mut p,
        &frame,
        style,
        &Shape::circle(cx, cy, r, 48),
        0.012,
        base,
        0.42,
        0.0,
    );
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        brush(
            frame.line(cx - r, cy, cx + r, cy),
            0.022,
            shadow,
            style.conc(0.7),
            style.water(0.3),
            0.5,
        ),
    );
    for squash in [0.42f32, -0.42] {
        let meridian: Vec<_> = (0..=24)
            .map(|i| {
                let t = i as f32 / 24.0;
                let a = -std::f32::consts::FRAC_PI_2 + t * std::f32::consts::PI;
                frame.pt(cx + r * squash * a.cos(), cy + r * a.sin())
            })
            .collect();
        p.at(
            0.56,
            brush(
                meridian,
                0.02,
                shadow,
                style.conc(0.6),
                style.water(0.28),
                0.55,
            ),
        );
    }
    if style.fine() {
        p.at(
            0.66,
            brush(
                frame.line(cx - r * 0.55, cy - r * 0.5, cx + r * 0.55, cy - r * 0.5),
                0.018,
                shadow,
                style.conc(0.4),
                style.water(0.22),
                0.6,
            ),
        );
    }
    p.settle(0.84, 3.0);
    style.scene(
        "world-globe",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// The GitHub silhouette: a cat head with two pointed ears, two eyes lifted
/// back out of the wash, and a tail curling away at the bottom left. Those
/// four marks are what make it that mark and not a circle. Nominative use,
/// for screens that name the service.
pub(super) fn github_mark(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let (cx, cy, r) = (0.5f32, 0.52f32, 0.34f32);
    let mut p = Painting::new(style.ticks(360));
    // The ears are triangular, not rounded: a bump that falls off smoothly
    // leaves a head with two swellings, which is not what anyone recognises.
    // A linear falloff keeps the tip sharp enough to survive the wash.
    let body = Shape::sampled(96, |t| {
        let a = t * TAU;
        let ear = |centre: f32| {
            let mut d = a - centre;
            while d > PI {
                d -= TAU;
            }
            while d < -PI {
                d += TAU;
            }
            (0.18 * (1.0 - (d / 0.42).abs())).max(0.0)
        };
        let out = r + ear(-2.29) + ear(-0.85);
        (cx + out * a.cos() * 1.02, cy + out * a.sin() * 0.95)
    });
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.46, 0.0);
    p.glaze(0.5, 0.1).clear_mask(0.5);
    // The tail sweeps out of the bottom left and curls back under.
    p.at(
        0.5,
        tapered(
            vec![
                frame.pt(0.37, 0.77),
                frame.pt(0.26, 0.85),
                frame.pt(0.17, 0.86),
                frame.pt(0.15, 0.93),
            ],
            (0.055, 0.022),
            base,
            style.conc(0.9),
            style.water(0.4),
            0.45,
        ),
    );
    // The eyes are taken back out of the wash rather than painted over it,
    // which is how they read on the mark itself.
    for x in [0.395f32, 0.605] {
        p.at(0.58, lift(vec![frame.pt(x, 0.5)], 0.052, 1.0, 0.3));
    }
    if style.fine() {
        p.at(
            0.66,
            brush(
                vec![frame.pt(0.5, 0.63)],
                0.026,
                shadow,
                style.conc(0.6),
                style.water(0.22),
                0.5,
            ),
        );
    }
    p.settle(0.84, 3.0);
    style.scene(
        "github-mark",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A tapered bar over a dot. The gap between them is the whole shape, so the
/// dot is placed clear of the bar rather than tucked under it.
pub(super) fn exclamation_mark(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let accent = role(palette, PigmentRole::Accent);
    let mut p = Painting::new(style.ticks(320));
    let bar = Shape(vec![
        (0.415, 0.13),
        (0.585, 0.13),
        (0.548, 0.60),
        (0.452, 0.60),
    ]);
    stencil_body(&mut p, &frame, style, &bar, 0.012, accent, 0.62, 0.0);
    p.glaze(0.55, 0.1).clear_mask(0.55);
    p.at(
        0.58,
        brush(
            vec![frame.pt(0.5, 0.79)],
            0.072,
            accent,
            style.conc(1.0),
            style.water(0.45),
            0.4,
        ),
    );
    p.settle(0.82, 3.0);
    style.scene(
        "exclamation-mark",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A rounded bubble with a tail running off its bottom-left corner, and three
/// dots inside. Without the tail it is a rounded rectangle like any other.
pub(super) fn chat_bubble(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape::rounded_rect(0.12, 0.18, 0.88, 0.64, 0.14);
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.3, 0.0);
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        tapered(
            vec![
                frame.pt(0.32, 0.6),
                frame.pt(0.24, 0.78),
                frame.pt(0.19, 0.87),
            ],
            (0.075, 0.016),
            base,
            style.conc(0.85),
            style.water(0.45),
            0.4,
        ),
    );
    if style.fine() {
        for x in [0.33f32, 0.5, 0.67] {
            p.at(
                0.6,
                brush(
                    vec![frame.pt(x, 0.41)],
                    0.034,
                    shadow,
                    style.conc(0.9),
                    style.water(0.25),
                    0.45,
                ),
            );
        }
    }
    p.settle(0.84, 3.0);
    style.scene(
        "chat-bubble",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A mobile handset stood on end: half as wide as it is tall, where every
/// other rounded body in the catalogue is wider than it is tall, so the
/// proportion carries most of the recognition. The screen is stencilled
/// rather than glazed over the body — an inset painted straight onto the wash
/// is only a shade apart from it, while a stencil gives it the same crisp rim
/// the body has.
pub(super) fn phone(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(360));
    let body = Shape::rounded_rect(0.27, 0.05, 0.73, 0.95, 0.1);
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.3, 0.0);
    p.glaze(0.36, 0.09).clear_mask(0.36);
    // The earpiece slot and the button below the screen go on the bare bezel
    // first: the bezel is a tenth of the artwork wide, and a mark laid after
    // the screen would have to clear its tideline. The slot waits for Medium
    // — at 48 px it blurs into the screen's top edge and greys the bezel it
    // is meant to punctuate.
    if style.fine() {
        p.at(
            0.38,
            brush(
                frame.line(0.44, 0.142, 0.56, 0.142),
                0.022,
                shadow,
                style.conc(1.0),
                style.water(0.22),
                0.4,
            ),
        );
    }
    p.at(
        0.42,
        brush(
            vec![frame.pt(0.5, 0.845)],
            0.034,
            shadow,
            style.conc(0.9),
            style.water(0.22),
            0.45,
        ),
    );
    if style.full() {
        p.at(
            0.46,
            brush(
                frame.line(0.742, 0.32, 0.742, 0.41),
                0.022,
                base,
                style.conc(0.9),
                style.water(0.22),
                0.45,
            ),
        );
    }
    // The screen keeps its stencil almost to the end. `ClearMask` lets the
    // film spread again, and a bezel this narrow has no room for the creep:
    // the deeper margin below the screen is what says which way up the thing
    // stands, and it only reads while it stays clear.
    let (top, bottom) = (0.292f32, 0.686);
    p.mask(
        0.5,
        Shape::rounded_rect(0.378, 0.268, 0.622, 0.71, 0.028).polygon(&frame),
        0.006,
    );
    p.at(
        0.5,
        brush(
            frame.hatch(0.3, 0.7, top, bottom, 7),
            frame.hatch_radius(top, bottom, 7),
            shadow,
            style.conc(1.7),
            style.water(0.17),
            0.7,
        ),
    );
    if style.fine() {
        // Glare across the glass, lifted while the screen is still stencilled.
        p.at(
            0.74,
            lift(frame.line(0.42, 0.43, 0.57, 0.29), 0.032, 0.6, 0.6),
        );
    }
    p.glaze(0.8, 0.1).clear_mask(0.82);
    p.settle(0.86, 3.0);
    style.scene(
        "phone",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
