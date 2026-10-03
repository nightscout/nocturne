//! Accents: restrained pieces that sit behind or beside interface text, so
//! their pigment stays light and their geometry keeps to the edges.

use nocturne_watercolour_core::domain::{Palette, Paper, PigmentRole, Point, Scene, SizeHint};

use super::geometry::Frame;
use super::{Painting, SQUARE, Style, brush, role, tapered, water};

pub(super) const TAB: SizeHint = SizeHint {
    width: 512,
    height: 64,
};

pub(super) const EDGE: SizeHint = SizeHint {
    width: 85,
    height: 512,
};

pub(super) const BANNER_3_1: SizeHint = SizeHint {
    width: 512,
    height: 171,
};

/// A loose disc of the base pigment with the accent and shadow dropped in
/// while wet; the host clips it to the avatar's shape.
pub(super) fn avatar_wash(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let accent = role(palette, PigmentRole::Accent);
    let shadow = role(palette, PigmentRole::Shadow);
    let (y0, y1) = (0.15, 0.79);
    let rows = if style.fine() { 7 } else { 5 };
    let mut p = Painting::new(style.ticks(320));
    p.mask(0.0, frame.circle(0.47, 0.47, 0.32, 48), 0.02);
    // The stencil owns the silhouette; the body only has to deliver pigment
    // inside it. Pre-wet the footprint, then hatch the pigment in so the
    // reveal has a path to pace. The turns sit outside the mask.
    p.at(
        0.0,
        water(
            frame.line(0.42, 0.44, 0.52, 0.5),
            0.3,
            style.water(0.75),
            0.15,
        ),
    );
    p.at(
        0.0,
        brush(
            frame.hatch(0.0, 1.0, y0, y1, rows),
            frame.hatch_radius(y0, y1, rows),
            base,
            style.conc(0.3),
            style.water(0.42),
            0.75,
        ),
    );
    p.at(
        0.05,
        brush(
            vec![frame.pt(0.6, 0.36)],
            0.09,
            accent,
            style.conc(0.7),
            style.water(0.35),
            0.8,
        ),
    );
    if style.fine() {
        p.at(
            0.1,
            brush(
                vec![frame.pt(0.36, 0.64)],
                0.07,
                shadow,
                style.conc(0.6),
                style.water(0.3),
                0.8,
            ),
        );
    }
    if style.full() {
        p.at(
            0.2,
            water(vec![frame.pt(0.5, 0.56)], 0.08, style.water(0.8), 0.6),
        );
    }
    p.settle(0.55, 3.5);
    style.scene(
        "avatar-wash",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// A painted line under a tab: slight wobble, dry-brush break-up where the
/// stroke starts and a pooled, heavier end where the brush lifts.
pub(super) fn tab_underline(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(TAB);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut stream = style.stream(2);
    // A smaller wobble on Small keeps the thin hairline continuous on the
    // coarser grid; the fine levels can carry the full hand-drawn sway.
    let wobble = if style.fine() { 0.06 } else { 0.03 };
    let path: Vec<Point> = [1.4, 2.8, 4.2, 5.9, 7.6]
        .iter()
        .map(|&x| frame.pt(x, 0.5 + stream.next_signed() * wobble))
        .collect();
    // Small sits under a 12-14 px label, so it is a pale hairline rather than a bar.
    let (radius, conc) = if style.fine() {
        ((0.1, 0.17), 0.9)
    } else {
        ((0.07, 0.11), 0.6)
    };
    let mut p = Painting::new(style.ticks(260));
    p.at(
        0.0,
        tapered(
            path.clone(),
            radius,
            base,
            style.conc(conc),
            style.water(0.45),
            0.3,
        ),
    );
    p.at(
        0.03,
        brush(
            vec![frame.pt(7.9, 0.53)],
            if style.fine() { 0.15 } else { 0.08 },
            shadow,
            style.conc(if style.fine() { 0.7 } else { 0.45 }),
            style.water(if style.fine() { 0.6 } else { 0.4 }),
            0.5,
        ),
    );
    if style.fine() {
        // Dry-brush start: separate bristle streaks that run into the stroke.
        for &(x0, x1, dy) in &[(0.5, 1.9, -0.14), (0.8, 2.2, 0.02), (0.7, 1.6, 0.16)] {
            p.at(
                0.0,
                brush(
                    frame.line(x0, 0.5 + dy, x1, 0.5 + dy * 0.6),
                    0.035,
                    base,
                    style.conc(0.75),
                    style.water(0.15),
                    0.3,
                ),
            );
        }
    }
    if style.full() {
        p.at(
            0.0,
            brush(
                frame.line(0.3, 0.7, 1.4, 0.66),
                0.022,
                base,
                style.conc(0.6),
                style.water(0.08),
                0.2,
            ),
        );
    }
    p.settle(0.5, 4.0);
    style.scene(
        "tab-underline",
        palette,
        TAB,
        Paper::hot_press(style.seed()),
        p.finish(),
    )
}

/// A soft vertical wash on the left edge that bleeds inward to nothing.
pub(super) fn selection_edge(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(EDGE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let mut p = Painting::new(style.ticks(300));
    // No mask: the whole strip is wetted with water that thins from the left
    // edge to nothing, so the pigment dropped along that edge bleeds inward
    // and the drying front walks in from the right without a boundary rim.
    p.at(
        0.0,
        water(frame.line(0.0, 0.0, 0.0, 1.0), 1.0, style.water(1.2), 1.0),
    );
    // Uneven along the length: two heavier stretches around a thin one.
    // Gaps between the segments are what the round caps fill, so no stretch
    // carries double pigment.
    let spine = [
        (0.0, 0.4, 0.7, 0.22),
        (0.44, 0.6, 0.35, 0.14),
        (0.64, 1.0, 0.7, 0.22),
    ];
    for &(y0, y1, conc, r) in &spine {
        p.at(
            0.0,
            brush(
                frame.line(0.0, y0, 0.0, y1),
                r,
                base,
                style.conc(conc),
                style.water(0.9),
                0.9,
            ),
        );
    }
    if style.fine() {
        for &(y, conc) in &[(0.24, 0.6), (0.8, 0.5)] {
            p.at(
                0.04,
                brush(
                    vec![frame.pt(0.03, y)],
                    0.04,
                    shadow,
                    style.conc(conc),
                    style.water(0.7),
                    0.7,
                ),
            );
        }
    }
    if style.full() {
        p.at(
            0.12,
            water(vec![frame.pt(0.05, 0.52)], 0.1, style.water(0.6), 0.6),
        );
    }
    p.settle(0.45, 2.5);
    style.scene(
        "selection-edge",
        palette,
        EDGE,
        Paper::hot_press(style.seed()),
        p.finish(),
    )
}

/// Two long tapered washes hugging the bottom-left and top-right edges,
/// light enough to sit under text. Each is a wedge thick at its corner and
/// thinning to nothing toward the middle, kept inside the outer quarter of
/// the banner by a band mask, so the centre stays clear for text.
pub(super) fn confirmation_background(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(BANNER_3_1);
    let base = role(palette, PigmentRole::BaseWash);
    let glow = role(palette, PigmentRole::Glow);
    let mut p = Painting::new(style.ticks(380));
    // Bottom-left sweep: a tapered wedge along the bottom edge, thick at the
    // corner and fading by 55 % of the width. The band mask holds its water
    // inside the bottom quarter so the centre stays clear for text.
    p.mask(0.0, frame.rect(0.0, 0.76, 1.7, 1.0), 0.03);
    p.at(
        0.0,
        tapered(
            frame.line(0.0, 0.985, 1.65, 0.985),
            (0.24, 0.06),
            base,
            style.conc(0.35),
            style.water(1.0),
            0.5,
        ),
    );
    if style.fine() {
        // Top-right sweep, wet-on-dry: the mirror image along the top edge,
        // thick at the far corner and fading toward the middle. Under
        // luminous compositing a pale glow glaze over clear paper reads as a
        // lit slab, so the dark ground gets a whisper of the base instead.
        let (second, second_conc) = if style.dark() {
            (base, 0.15)
        } else {
            (glow, 0.6)
        };
        p.glaze(0.5, 0.1);
        p.mask(0.5, frame.rect(1.35, 0.0, frame.aspect, 0.2), 0.03);
        p.at(
            0.5,
            tapered(
                frame.line(frame.aspect, 0.015, 1.4, 0.015),
                (0.24, 0.06),
                second,
                style.conc(second_conc),
                style.water(1.0),
                0.5,
            ),
        );
    }
    p.settle(0.8, 4.0);
    style.scene(
        "confirmation-background",
        palette,
        BANNER_3_1,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
