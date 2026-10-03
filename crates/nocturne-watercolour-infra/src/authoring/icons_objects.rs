//! Object icons: apple, pizza slice, spanner, suitcase, paint palette.

use std::f32::consts::TAU;

use nocturne_watercolour_core::domain::{Palette, Paper, PigmentRole, Scene};

use super::geometry::Frame;
use super::{Painting, SQUARE, Shape, Style, brush, role, stencil_body, water};

/// Two lobes meeting in a dimple at the top, with a stem and one leaf. The
/// dimple is what separates an apple from a circle, so it is cut deeper than
/// a real one.
pub(super) fn apple(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let accent = role(palette, PigmentRole::Accent);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape::sampled(64, |t| {
        let a = t * TAU;
        // A circle pinched at the top and slightly waisted at the bottom.
        let dimple = 0.10 * (-a.sin().min(0.0)).powf(6.0);
        let lobe = 1.0 + 0.06 * (2.0 * a).cos();
        (
            0.5 + 0.33 * lobe * a.cos(),
            0.58 + 0.31 * lobe * a.sin() + dimple,
        )
    });
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.46, 0.0);
    if style.fine() {
        p.at(
            0.2,
            brush(
                frame.line(0.33, 0.48, 0.3, 0.62),
                0.05,
                accent,
                style.conc(0.4),
                style.water(0.5),
                0.85,
            ),
        );
    }
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        brush(
            frame.line(0.5, 0.29, 0.53, 0.17),
            0.022,
            shadow,
            style.conc(0.85),
            style.water(0.3),
            0.4,
        ),
    );
    p.at(
        0.56,
        brush(
            frame.line(0.55, 0.2, 0.68, 0.16),
            0.038,
            accent,
            style.conc(0.7),
            style.water(0.35),
            0.6,
        ),
    );
    p.settle(0.82, 3.0);
    style.scene(
        "apple",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A wedge with its crust bowed outward at the wide end, and three toppings.
pub(super) fn pizza_slice(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let accent = role(palette, PigmentRole::Accent);
    let shadow = role(palette, PigmentRole::Shadow);
    let (tip, left, right, crust) = ((0.5f32, 0.14f32), 0.13f32, 0.87f32, 0.8f32);
    let mut p = Painting::new(style.ticks(340));
    let wedge = Shape(
        std::iter::once(tip)
            .chain((0..=16).map(|i| {
                let t = i as f32 / 16.0;
                let x = left + (right - left) * t;
                // The crust bows away from the tip.
                (x, crust + 0.07 * (t * std::f32::consts::PI).sin())
            }))
            .collect(),
    );
    stencil_body(&mut p, &frame, style, &wedge, 0.012, base, 0.44, 0.0);
    p.at(
        0.15,
        brush(
            frame.line(0.16, 0.82, 0.84, 0.82),
            0.055,
            accent,
            style.conc(0.55),
            style.water(0.35),
            0.6,
        ),
    );
    p.glaze(0.55, 0.1).clear_mask(0.55);
    if style.fine() {
        for (x, y) in [(0.44f32, 0.44f32), (0.6, 0.58), (0.39, 0.65)] {
            p.at(
                0.58,
                brush(
                    vec![frame.pt(x, y)],
                    0.036,
                    shadow,
                    style.conc(0.8),
                    style.water(0.3),
                    0.5,
                ),
            );
        }
    }
    p.settle(0.84, 3.0);
    style.scene(
        "pizza-slice",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// An open jaw at one end of a diagonal shaft and a closed ring at the other:
/// the two ends are what name the tool, so both are cut into the stencil.
pub(super) fn spanner(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(360));
    let (ax, ay, bx, by) = (0.27f32, 0.73f32, 0.73f32, 0.27f32);
    let (dx, dy) = (bx - ax, by - ay);
    let len = (dx * dx + dy * dy).sqrt();
    let (ux, uy) = (dx / len, dy / len);
    let (nx, ny) = (-uy, ux);
    let half = 0.075f32;
    let shaft = Shape(vec![
        (ax + nx * half, ay + ny * half),
        (bx + nx * half, by + ny * half),
        (bx - nx * half, by - ny * half),
        (ax - nx * half, ay - ny * half),
    ]);
    stencil_body(&mut p, &frame, style, &shaft, 0.012, base, 0.5, 0.0);
    p.glaze(0.4, 0.1).clear_mask(0.4);
    // The heads: a ring at the low end, an open C at the high end.
    p.at(
        0.42,
        brush(
            frame.ring(ax - ux * 0.06, ay - uy * 0.06, 0.1, 28),
            0.05,
            base,
            style.conc(0.7),
            style.water(0.5),
            0.5,
        ),
    );
    let jaw: Vec<_> = (0..=22)
        .map(|i| {
            let a = 0.85 + i as f32 / 22.0 * (TAU - 1.7);
            frame.pt(
                bx + ux * 0.06 + 0.105 * a.cos(),
                by + uy * 0.06 + 0.105 * a.sin(),
            )
        })
        .collect();
    p.at(
        0.5,
        brush(jaw, 0.052, base, style.conc(0.7), style.water(0.5), 0.5),
    );
    if style.fine() {
        p.at(
            0.62,
            brush(
                frame.line(ax + 0.04, ay - 0.04, bx - 0.04, by + 0.04),
                0.02,
                shadow,
                style.conc(0.45),
                style.water(0.25),
                0.7,
            ),
        );
    }
    p.settle(0.84, 3.0);
    style.scene(
        "spanner",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A squat case under its handle, split by a latch band.
pub(super) fn suitcase(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(340));
    let body = Shape::rounded_rect(0.12, 0.34, 0.88, 0.84, 0.08);
    stencil_body(&mut p, &frame, style, &body, 0.012, base, 0.44, 0.0);
    p.at(
        0.16,
        brush(
            frame.line(0.1, 0.59, 0.9, 0.59),
            0.04,
            shadow,
            style.conc(0.5),
            style.water(0.3),
            0.55,
        ),
    );
    p.glaze(0.5, 0.1).clear_mask(0.5);
    // The handle is a squared arch: two uprights and a top bar.
    p.at(
        0.5,
        brush(
            vec![
                frame.pt(0.39, 0.34),
                frame.pt(0.39, 0.22),
                frame.pt(0.61, 0.22),
                frame.pt(0.61, 0.34),
            ],
            0.028,
            shadow,
            style.conc(0.85),
            style.water(0.3),
            0.45,
        ),
    );
    if style.fine() {
        p.at(
            0.6,
            brush(
                vec![frame.pt(0.5, 0.59)],
                0.03,
                shadow,
                style.conc(0.8),
                style.water(0.25),
                0.45,
            ),
        );
    }
    p.settle(0.82, 3.0);
    style.scene(
        "suitcase",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A kidney-shaped board with a thumb hole and four dabs of colour. The dabs
/// are the icon: without them the board is a blob.
pub(super) fn paint_palette(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let accent = role(palette, PigmentRole::Accent);
    let glow = role(palette, PigmentRole::Glow);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(360));
    let board = Shape::sampled(56, |t| {
        let a = t * TAU;
        let r = 0.36 * (1.0 + 0.10 * (a + 0.6).cos() - 0.05 * (2.0 * a).sin());
        (0.5 + r * a.cos() * 1.02, 0.52 + r * a.sin() * 0.92)
    });
    stencil_body(&mut p, &frame, style, &board, 0.014, base, 0.4, 0.0);
    p.glaze(0.45, 0.1).clear_mask(0.45);
    // The thumb hole, lifted back out of the wash.
    // The hole is what makes the board a palette, so it is lifted hard and
    // placed where the thumb would go rather than in the middle of the dabs.
    p.at(
        0.46,
        super::lift(vec![frame.pt(0.63, 0.63)], 0.095, 1.0, 0.25),
    );
    for (i, (x, y, pigment)) in [
        (0.33f32, 0.35f32, accent),
        (0.5, 0.29, glow),
        (0.66, 0.36, shadow),
        (0.3, 0.55, accent),
    ]
    .into_iter()
    .enumerate()
    {
        p.at(
            0.52 + i as f32 * 0.05,
            brush(
                vec![frame.pt(x, y)],
                0.052,
                pigment,
                style.conc(1.6),
                style.water(0.22),
                0.4,
            ),
        );
    }
    if style.full() {
        p.at(
            0.74,
            water(vec![frame.pt(0.5, 0.5)], 0.08, style.water(0.5), 0.7),
        );
    }
    p.settle(0.86, 3.0);
    style.scene(
        "paint-palette",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
