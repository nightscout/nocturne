//! Hero icons: square artworks that must read as their object at 32-48 px.
//! Each keeps one bold silhouette at `Small` and adds its secondary marks
//! (halos, blooms, line hints) only when `Style::fine`.

use nocturne_watercolour_core::domain::{
    LiftStroke, Operation, Palette, Paper, PigmentRole, RadiusProfile, Scene, StrokeSpan,
};

use super::geometry::{Crescent, Frame, bell_outline};
use super::{Painting, SQUARE, Style, brush, role, tapered, water};

/// Pale gold body whose convex side bleeds into a pre-wetted halo while the
/// concave edge dries crisp against the mask, with a whisper of the shadow
/// pigment along that edge so the moon holds its shape on a light ground.
pub(super) fn crescent_moon(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let glow = role(palette, PigmentRole::Glow);
    let shadow = role(palette, PigmentRole::Shadow);
    let crescent = Crescent::at(0.5, 0.5, 0.27, 0.3);
    let spine = frame.map(&crescent.spine(11));
    let mid = spine.len() / 2;
    let mut p = Painting::new(style.ticks(360));
    let reach = if style.fine() { 0.13 } else { 0.05 };
    p.mask(0.0, frame.map(&crescent.mask_outline(reach, 96)), 0.02);
    if style.fine() {
        let halo = frame.map(&crescent.outer_halo(0.05, 13));
        p.at(0.0, water(halo.clone(), 0.1, style.water(0.9), 1.0));
        p.at(
            0.0,
            brush(halo, 0.09, glow, style.conc(0.7), style.water(0.6), 1.0),
        );
    }
    if style.full() && !style.dark() {
        // A trace of the warm accent in the thick of the body keeps the
        // moon from reading as plain cream once the glaze thins.
        p.at(
            0.03,
            brush(
                vec![spine[mid]],
                0.035,
                role(palette, PigmentRole::Accent),
                style.conc(0.12),
                style.water(0.2),
                0.9,
            ),
        );
    }
    let (thin, thick) = if style.fine() {
        (0.018, 0.055)
    } else {
        (0.03, 0.062)
    };
    let body = |path: Vec<_>, radius: (f32, f32)| {
        let (conc, wet) = style.glow(1.3, 1.0);
        tapered(path, radius, glow, conc, wet, 0.3)
    };
    p.at(0.0, body(spine[..=mid].to_vec(), (thin, thick)));
    p.at(0.0, body(spine[mid..].to_vec(), (thick, thin)));
    if style.fine() {
        p.at(
            0.05,
            brush(
                vec![spine[mid - 1], spine[mid + 1]],
                0.04,
                glow,
                style.conc(1.2),
                style.water(0.35),
                0.7,
            ),
        );
        p.at(
            0.12,
            brush(
                vec![spine[2], spine[3]],
                0.03,
                glow,
                style.conc(1.0),
                style.water(0.3),
                0.8,
            ),
        );
    }
    if !style.dark() {
        let concave = frame.map(&crescent.concave_edge(0.4, 11));
        let (rad, conc) = if style.fine() {
            (0.024, 0.75)
        } else {
            (0.028, 0.6)
        };
        p.at(
            0.1,
            brush(
                concave[2..=8].to_vec(),
                rad,
                shadow,
                style.conc(conc),
                style.water(0.15),
                0.9,
            ),
        );
    }
    p.settle(0.5, 3.0);
    style.scene(
        "crescent-moon",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Dome and lip stencilled from one wash, then a wet-on-dry lip band and a
/// clapper below it.
pub(super) fn alarm_bell(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let (top, lip, lip_hw, lip_depth) = (0.2, 0.68, 0.3, 0.06);
    let mut p = Painting::new(style.ticks(340));
    p.mask(
        0.0,
        bell_outline(&frame, 0.5, top, lip, lip_hw, lip_depth),
        0.012,
    );
    // The stencil owns the silhouette; the body only has to deliver pigment
    // inside it. One wide stamp would do that on the reveal's first step, so
    // the footprint is pre-wet in a single pass — water alone leaves no mark —
    // and the pigment is hatched in, giving the pen a path long enough to
    // pace. The turns at x 0.06 and 0.94 fall outside the mask.
    let (band_top, band_bottom) = (top - 0.01, lip + lip_depth * 1.2);
    let rows = if style.fine() { 7 } else { 5 };
    p.at(
        0.0,
        water(
            frame.line(0.5, 0.32, 0.5, 0.62),
            0.42,
            style.water(0.75),
            0.15,
        ),
    );
    p.at(
        0.0,
        brush(
            frame.hatch(0.06, 0.94, band_top, band_bottom, rows),
            frame.hatch_radius(band_top, band_bottom, rows),
            base,
            style.conc(0.42),
            style.water(0.42),
            0.75,
        ),
    );
    p.at(
        0.05,
        brush(
            frame.line(0.3, 0.63, 0.7, 0.63),
            0.09,
            shadow,
            style.conc(0.45),
            style.water(0.3),
            0.9,
        ),
    );
    if style.full() {
        p.at(
            0.08,
            Operation::Lift(LiftStroke {
                path: vec![frame.pt(0.38, 0.32), frame.pt(0.33, 0.5)],
                radius: RadiusProfile::uniform(0.035),
                strength: 0.55,
                softness: 0.8,
                span: StrokeSpan::FULL,
            }),
        );
    }
    p.glaze(0.5, 0.1).clear_mask(0.5);
    p.at(
        0.5,
        brush(
            frame.line(0.19, lip + lip_depth * 0.5, 0.81, lip + lip_depth * 0.5),
            0.03,
            shadow,
            style.conc(0.85),
            style.water(0.4),
            0.4,
        ),
    );
    p.at(
        0.5,
        brush(
            vec![frame.pt(0.5, 0.82)],
            0.045,
            shadow,
            style.conc(0.85),
            style.water(0.35),
            0.5,
        ),
    );
    if style.fine() {
        p.at(
            0.5,
            brush(
                vec![frame.pt(0.5, 0.165)],
                0.028,
                shadow,
                style.conc(0.6),
                style.water(0.3),
                0.5,
            ),
        );
    }
    p.settle(0.8, 3.0);
    style.scene(
        "alarm-bell",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Two rings, the second glazed wet-on-dry so the overlaps darken.
pub(super) fn linked_rings(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let accent = role(palette, PigmentRole::Accent);
    let shadow = role(palette, PigmentRole::Shadow);
    let r = 0.2;
    let thickness = if style.fine() { 0.045 } else { 0.062 };
    let mut p = Painting::new(style.ticks(400));
    let ring = |cx: f32, pigment: usize| {
        brush(
            frame.ring(cx, 0.5, r, 32),
            thickness,
            pigment,
            style.conc(0.8),
            style.water(0.8),
            0.35,
        )
    };
    p.at(0.0, ring(0.38, base));
    if style.fine() {
        p.at(
            0.06,
            brush(
                vec![frame.pt(0.3, 0.66), frame.pt(0.4, 0.7)],
                0.03,
                shadow,
                style.conc(0.5),
                style.water(0.2),
                0.9,
            ),
        );
    }
    p.glaze(0.5, 0.1);
    p.at(0.5, ring(0.62, accent));
    if style.fine() {
        p.at(
            0.56,
            brush(
                vec![frame.pt(0.72, 0.34), frame.pt(0.78, 0.42)],
                0.03,
                shadow,
                style.conc(0.4),
                style.water(0.2),
                0.9,
            ),
        );
    }
    if style.full() {
        p.at(
            0.58,
            water(vec![frame.pt(0.62, 0.3)], 0.06, style.water(0.6), 0.6),
        );
    }
    p.settle(0.8, 3.0);
    style.scene(
        "linked-rings",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Two offset sheets, the front one glazed over the back so the overlap
/// darkens behind a crisp edge; line hints are thin strokes on the front.
pub(super) fn report_pages(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let back = [0.28, 0.16, 0.64, 0.70];
    let front = [0.36, 0.30, 0.72, 0.84];
    let mut p = Painting::new(style.ticks(400));
    let fill = |rect: [f32; 4], pigment: usize, conc: f32| {
        let (x0, y0, x1, y1) = (rect[0], rect[1], rect[2], rect[3]);
        let cx = (x0 + x1) * 0.5;
        let rows = if style.fine() { 7 } else { 5 };
        let radius = frame.hatch_radius(y0, y1, rows);
        // The stencil owns the silhouette; the body only has to deliver
        // pigment inside it. Pre-wet the footprint, then hatch the pigment
        // in so the reveal has a path to pace. The turns sit outside the mask.
        (
            water(
                frame.line(cx, y0 + 0.12, cx, y1 - 0.12),
                0.32,
                style.water(0.75),
                0.15,
            ),
            brush(
                frame.hatch(x0 - radius - 0.02, x1 + radius + 0.02, y0, y1, rows),
                radius,
                pigment,
                style.conc(conc * 0.6),
                style.water(0.42),
                0.75,
            ),
        )
    };
    p.mask(0.0, frame.rect(back[0], back[1], back[2], back[3]), 0.01);
    let (back_wet, back_hatch) = fill(back, shadow, 0.45);
    p.at(0.0, back_wet);
    p.at(0.0, back_hatch);
    p.glaze(0.45, 0.1);
    p.mask(
        0.45,
        frame.rect(front[0], front[1], front[2], front[3]),
        0.01,
    );
    let (front_wet, front_hatch) = fill(front, base, 0.55);
    p.at(0.45, front_wet);
    p.at(0.45, front_hatch);
    if style.fine() {
        p.at(
            0.5,
            brush(
                vec![frame.pt(0.62, 0.78)],
                0.07,
                shadow,
                style.conc(0.35),
                style.water(0.25),
                0.9,
            ),
        );
        p.glaze(0.75, 0.1);
        let lines: &[(f32, f32)] = if style.full() {
            &[(0.5, 0.63), (0.58, 0.63), (0.66, 0.58), (0.74, 0.52)]
        } else {
            &[(0.52, 0.63), (0.62, 0.63), (0.72, 0.56)]
        };
        for &(y, x1) in lines {
            p.at(
                0.75,
                brush(
                    frame.line(0.43, y, x1, y),
                    0.011,
                    shadow,
                    style.conc(0.9),
                    style.water(0.15),
                    0.3,
                ),
            );
        }
    }
    p.settle(0.9, 3.0);
    style.scene(
        "report-pages",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Ring and handle over a faintly tinted lens.
pub(super) fn magnifying_glass(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let glow = role(palette, PigmentRole::Glow);
    let (cx, cy, r) = (0.42, 0.42, 0.21);
    let thickness = if style.fine() { 0.05 } else { 0.062 };
    let mut p = Painting::new(style.ticks(380));
    let ring_at = if style.fine() && !style.dark() {
        let lens_r = r - 0.02;
        let rows = 7;
        let radius = frame.hatch_radius(cy - lens_r, cy + lens_r, rows);
        p.mask(0.0, frame.circle(cx, cy, lens_r, 32), 0.008);
        // The stencil owns the silhouette; the body only has to deliver pigment
        // inside it. Pre-wet the footprint, then hatch the pigment in so the
        // reveal has a path to pace. The turns sit outside the mask.
        p.at(
            0.0,
            water(
                frame.line(cx - 0.15, cy, cx + 0.15, cy),
                0.3,
                style.water(0.75),
                0.15,
            ),
        );
        p.at(
            0.0,
            brush(
                frame.hatch(
                    cx - lens_r - radius,
                    cx + lens_r + radius,
                    cy - lens_r,
                    cy + lens_r,
                    rows,
                ),
                radius,
                glow,
                style.conc(0.25 * 0.6),
                style.water(0.42),
                0.75,
            ),
        );
        p.at(
            0.05,
            brush(
                vec![frame.pt(cx + 0.07, cy + 0.07)],
                0.08,
                base,
                style.conc(0.2),
                style.water(0.3),
                0.9,
            ),
        );
        p.glaze(0.45, 0.1).clear_mask(0.45);
        0.45
    } else {
        0.0
    };
    p.at(
        ring_at,
        brush(
            frame.ring(cx, cy, r, 32),
            thickness,
            base,
            style.conc(0.85),
            style.water(0.7),
            0.35,
        ),
    );
    let along = std::f32::consts::FRAC_1_SQRT_2;
    p.at(
        ring_at,
        tapered(
            frame.line(cx + r * along * 0.97, cy + r * along * 0.97, 0.83, 0.83),
            (0.055, 0.045),
            shadow,
            style.conc(0.9),
            style.water(0.5),
            0.35,
        ),
    );
    if style.fine() {
        p.at(
            ring_at + 0.05,
            brush(
                vec![frame.pt(cx + r * along, cy + r * along)],
                0.045,
                shadow,
                style.conc(0.5),
                style.water(0.2),
                0.9,
            ),
        );
    }
    p.settle(0.8, 3.0);
    style.scene(
        "magnifying-glass",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// One tapered check stroke that pools where the brush lifts.
pub(super) fn confirmation_mark(style: &Style, palette: &Palette) -> Scene {
    let frame = Frame::new(SQUARE);
    let base = role(palette, PigmentRole::BaseWash);
    let shadow = role(palette, PigmentRole::Shadow);
    let path = vec![
        frame.pt(0.2, 0.54),
        frame.pt(0.42, 0.76),
        frame.pt(0.82, 0.26),
    ];
    let radius = if style.fine() {
        (0.035, 0.062)
    } else {
        (0.05, 0.075)
    };
    let mut p = Painting::new(style.ticks(300));
    p.at(
        0.0,
        tapered(path, radius, base, style.conc(0.9), style.water(0.8), 0.3),
    );
    p.at(
        0.03,
        brush(
            vec![frame.pt(0.8, 0.29)],
            0.055,
            shadow,
            style.conc(0.6),
            style.water(0.7),
            0.6,
        ),
    );
    if style.fine() {
        p.at(
            0.1,
            water(vec![frame.pt(0.42, 0.74)], 0.05, style.water(0.5), 0.6),
        );
    }
    p.settle(0.6, 3.0);
    style.scene(
        "confirmation-mark",
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}
