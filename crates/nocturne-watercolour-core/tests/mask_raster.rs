//! Bit-identity snapshot of `rasterize_mask_aspect`. Each case hashes the
//! output (FNV-1a over the `f32::to_bits` of every element) and asserts it
//! against a literal captured from the original implementation, so any change
//! that alters the rasterised output, however subtly, fails here.

use nocturne_watercolour_core::domain::paint::rasterize_mask_aspect;
use nocturne_watercolour_core::domain::{Mask, Point};

fn circle(centre: (f32, f32), radius: f32, samples: usize) -> Vec<Point> {
    (0..samples)
        .map(|i| {
            let a = i as f32 / samples as f32 * std::f32::consts::TAU;
            Point::new(centre.0 + radius * a.cos(), centre.1 + radius * a.sin())
        })
        .collect()
}

/// A concave five-pointed star: five outer points and five inner notches.
fn star() -> Vec<Point> {
    (0..10)
        .map(|i| {
            let r = if i % 2 == 0 { 0.3 } else { 0.12 };
            let a = i as f32 / 10.0 * std::f32::consts::TAU + std::f32::consts::FRAC_PI_2;
            Point::new(0.5 + r * a.cos(), 0.5 + r * a.sin())
        })
        .collect()
}

fn hash(out: &[f32]) -> u64 {
    let mut h = 0xcbf2_9ce4_8422_2325u64;
    for v in out {
        for byte in v.to_bits().to_le_bytes() {
            h ^= byte as u64;
            h = h.wrapping_mul(0x0000_0100_0000_01b3);
        }
    }
    h
}

/// `(name, mask)` in the same order as `EXPECTED`.
fn masks() -> Vec<(&'static str, Mask)> {
    let outlines: Vec<(&'static str, Vec<Point>)> = vec![
        ("circle16", circle((0.5, 0.5), 0.35, 16)),
        ("circle96", circle((0.5, 0.5), 0.35, 96)),
        ("circle400", circle((0.5, 0.5), 0.35, 400)),
        ("star", star()),
    ];
    let mut out = Vec::new();
    for (name, points) in outlines {
        for feather in [0.0, 0.05] {
            out.push((
                name,
                Mask::Polygon {
                    points: points.clone(),
                    feather,
                },
            ));
            out.push((
                name,
                Mask::Path {
                    points: points.clone(),
                    radius: 0.03,
                    feather,
                },
            ));
        }
    }
    out
}

/// For each mask in `masks()`, `(side, aspect, hash)` at `[128, 384]` x
/// `[1.0, 2.0]`, captured from the pre-acceleration implementation.
const EXPECTED: [(&str, u32, f32, u64); 64] = [
    ("circle16", 128, 1.0, 0xd309a9ec0cb40325),
    ("circle16", 128, 2.0, 0xd309a9ec0cb40325),
    ("circle16", 384, 1.0, 0x9ce29c55c2af22c5),
    ("circle16", 384, 2.0, 0x9ce29c55c2af22c5),
    ("circle16", 128, 1.0, 0x3da6187b33f907d5),
    ("circle16", 128, 2.0, 0xbbe1eca670988e05),
    ("circle16", 384, 1.0, 0xf527adafc9350058),
    ("circle16", 384, 2.0, 0xcb27e085f8ae5e55),
    ("circle16", 128, 1.0, 0x377245954fea4bb3),
    ("circle16", 128, 2.0, 0xd7ac1b2e89877b31),
    ("circle16", 384, 1.0, 0x29ad06c40cdac11c),
    ("circle16", 384, 2.0, 0x70f14dd2319f3276),
    ("circle16", 128, 1.0, 0x081ed526ae124a10),
    ("circle16", 128, 2.0, 0x7ed56edd039db624),
    ("circle16", 384, 1.0, 0x171299a3791a4826),
    ("circle16", 384, 2.0, 0xd98069f5add928a1),
    ("circle96", 128, 1.0, 0xc7b1b22d5a1c4ae5),
    ("circle96", 128, 2.0, 0xc7b1b22d5a1c4ae5),
    ("circle96", 384, 1.0, 0x78ea7f68ff2eea85),
    ("circle96", 384, 2.0, 0x78ea7f68ff2eea85),
    ("circle96", 128, 1.0, 0x2fb35be28c425c18),
    ("circle96", 128, 2.0, 0xbcb1815c002ab5a5),
    ("circle96", 384, 1.0, 0x593310d762a90125),
    ("circle96", 384, 2.0, 0xbc0e3453747d83c5),
    ("circle96", 128, 1.0, 0x79a5227056399f7c),
    ("circle96", 128, 2.0, 0x7489478ed5ea0051),
    ("circle96", 384, 1.0, 0x1f933297fce0302c),
    ("circle96", 384, 2.0, 0x12af334b2f27256c),
    ("circle96", 128, 1.0, 0xf9fdc0343318f63d),
    ("circle96", 128, 2.0, 0xbd216f0cf781a23e),
    ("circle96", 384, 1.0, 0xd597baf029cc73b8),
    ("circle96", 384, 2.0, 0x2b4b8521dfcb8c75),
    ("circle400", 128, 1.0, 0x461a833a202763a5),
    ("circle400", 128, 2.0, 0x461a833a202763a5),
    ("circle400", 384, 1.0, 0xbdba3422289afd85),
    ("circle400", 384, 2.0, 0xbdba3422289afd85),
    ("circle400", 128, 1.0, 0x54a605f26072a005),
    ("circle400", 128, 2.0, 0xa4dc3709200e8a05),
    ("circle400", 384, 1.0, 0xfb031e46da4dcc25),
    ("circle400", 384, 2.0, 0xba5fb504850489e5),
    ("circle400", 128, 1.0, 0xead56233100c94b4),
    ("circle400", 128, 2.0, 0xc492b72306d4a362),
    ("circle400", 384, 1.0, 0xa69a6b129aa4cfeb),
    ("circle400", 384, 2.0, 0xd23f2fc53ab39501),
    ("circle400", 128, 1.0, 0xc6fa2687e0f65277),
    ("circle400", 128, 2.0, 0x57a256a4c27afa54),
    ("circle400", 384, 1.0, 0xb1ab9cb7872ad89c),
    ("circle400", 384, 2.0, 0xa3eaeeeeb9993664),
    ("star", 128, 1.0, 0xba60ebc86ea352f5),
    ("star", 128, 2.0, 0xba60ebc86ea352f5),
    ("star", 384, 1.0, 0x7bb366f9c93144c5),
    ("star", 384, 2.0, 0x7bb366f9c93144c5),
    ("star", 128, 1.0, 0xb93d354d27e1ddf5),
    ("star", 128, 2.0, 0x86e878335674fd85),
    ("star", 384, 1.0, 0xcee77770aac529f8),
    ("star", 384, 2.0, 0xf034f48ad23b3a48),
    ("star", 128, 1.0, 0x3e1a281bdaffb522),
    ("star", 128, 2.0, 0x4e83d5750af6117a),
    ("star", 384, 1.0, 0x6e7336aedc7be18b),
    ("star", 384, 2.0, 0x083a83d1f27057f7),
    ("star", 128, 1.0, 0x668a50662623d30c),
    ("star", 128, 2.0, 0xf167ab7ff422ddc0),
    ("star", 384, 1.0, 0x207e70e115f016e8),
    ("star", 384, 2.0, 0x9645f0a998b121b1),
];

/// A polygon with fewer than three points has an empty row-edge table but a
/// non-empty bounding box, so a cell centre can fall on it. The fallback must
/// yield an empty edge list per row (never a panic) and the feathered distance
/// to the degenerate shape, which is what `Mask::Path` with `radius: 0.0`
/// computes over the same points.
#[test]
fn degenerate_polygon_masks_do_not_panic() {
    let points = [
        Point::new(0.25, 33.0 / 128.0),
        Point::new(0.75, 33.0 / 128.0),
    ];
    let polygon = Mask::Polygon {
        points: points.to_vec(),
        feather: 0.05,
    };
    let single = Mask::Polygon {
        points: vec![Point::new(0.5, 33.0 / 128.0)],
        feather: 0.05,
    };
    let path = Mask::Path {
        points: points.to_vec(),
        radius: 0.0,
        feather: 0.05,
    };

    let single_out = rasterize_mask_aspect(&single, 64, 64, 1.0);
    assert!(single_out.iter().all(|&v| (0.0..=1.0).contains(&v)));

    let poly_out = rasterize_mask_aspect(&polygon, 64, 64, 1.0);
    assert!(poly_out.iter().all(|&v| (0.0..=1.0).contains(&v)));
    // Cell (32, 16) has its centre exactly on the segment at y = 33/128.
    assert_eq!(poly_out[16 * 64 + 32], 1.0);

    let path_out = rasterize_mask_aspect(&path, 64, 64, 1.0);
    assert_eq!(poly_out, path_out);
}

#[test]
fn output_hashes_match_the_snapshot() {
    let mut i = 0;
    for (name, mask) in masks() {
        for side in [128, 384] {
            for aspect in [1.0, 2.0] {
                let (want_name, want_side, want_aspect, want) = EXPECTED[i];
                i += 1;
                assert_eq!(want_name, name, "table order mismatch at case {i}");
                assert_eq!(want_side, side, "table order mismatch at case {i}");
                assert_eq!(want_aspect, aspect, "table order mismatch at case {i}");
                let h = hash(&rasterize_mask_aspect(&mask, side, side, aspect));
                assert_eq!(h, want, "{name} at {side}x{side} aspect {aspect}");
            }
        }
    }
    assert_eq!(i, EXPECTED.len());
}
