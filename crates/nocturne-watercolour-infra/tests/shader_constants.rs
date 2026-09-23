//! Every scalar `const` the WGSL shaders declare mirrors a Rust constant of
//! the CPU reference. The parity test only catches a drifted literal through
//! its mean error, which a small drift slips under, so each is compared here.

use nocturne_watercolour_core::domain::{optics, paint, palette, sim, swirl};

const SHADERS: [(&str, &str); 3] = [
    (
        "render.wgsl",
        include_str!("../src/gpu/shaders/render.wgsl"),
    ),
    (
        "common.wgsl",
        include_str!("../src/gpu/shaders/common.wgsl"),
    ),
    ("flow.wgsl", include_str!("../src/gpu/shaders/flow.wgsl")),
];

/// Declared in `common.wgsl` and owned by the transfer pass, which keeps its
/// own mirror check.
const NOT_CHECKED_HERE: [&str; 2] = ["CARRY_MIN", "CARRY_REACH"];

fn rust_value(name: &str) -> Option<f64> {
    let v = match name {
        "MAX_BETA" => optics::MAX_BETA as f64,
        "ALPHA_SOFTNESS" => optics::ALPHA_SOFTNESS as f64,
        "LUMINOUS_ALPHA_TOE" => optics::LUMINOUS_ALPHA_TOE as f64,
        "LUMINOUS_ALPHA_HALF" => optics::LUMINOUS_ALPHA_HALF as f64,
        "LUMINOUS_ALPHA_MAX" => optics::LUMINOUS_ALPHA_MAX as f64,
        "LUMINOUS_ALPHA_GRAIN" => optics::LUMINOUS_ALPHA_GRAIN as f64,
        "LUMINOUS_COLOUR_FLOOR" => optics::LUMINOUS_COLOUR_FLOOR as f64,
        "LUMINOUS_COLOUR_CEILING" => optics::LUMINOUS_COLOUR_CEILING as f64,
        "LUMINOUS_GRAIN_STRENGTH" => optics::LUMINOUS_GRAIN_STRENGTH as f64,
        "LUMINOUS_CHROMA_GAIN" => optics::LUMINOUS_CHROMA_GAIN as f64,
        "LUMINOUS_PALE_LIFT" => optics::LUMINOUS_PALE_LIFT as f64,
        "LUMINOUS_EDGE_LO" => optics::LUMINOUS_EDGE_LO as f64,
        "LUMINOUS_EDGE_HI" => optics::LUMINOUS_EDGE_HI as f64,
        "LUMINOUS_MASK_THICKNESS" => optics::LUMINOUS_MASK_THICKNESS as f64,
        "MASK_MAJORITY" => optics::MASK_MAJORITY as f64,
        "MASK_THIN" => optics::MASK_THIN as f64,
        "MAX_PIGMENTS" => palette::MAX_PIGMENTS as f64,
        "DRAIN_DEPTH" => sim::DRAIN_DEPTH as f64,
        "DRAIN_MIN" => sim::DRAIN_MIN as f64,
        "DRAIN_MAX" => sim::DRAIN_MAX as f64,
        "STROKE_WATER_PAPER_GAIN" => paint::STROKE_WATER_PAPER_GAIN as f64,
        "SWIRL_DRIFT_SKEW" => swirl::SWIRL_DRIFT_SKEW as f64,
        "SWIRL_FACE_LIMIT" => sim::SWIRL_FACE_LIMIT as f64,
        "SWIRL_SUBSTEPS" => sim::SWIRL_SUBSTEPS as f64,
        _ => return None,
    };
    Some(v)
}

/// `const NAME: f32 = 0.5;` or `const NAME: u32 = 5u;` at the start of a line.
fn declared(source: &str) -> Vec<(String, f64)> {
    source
        .lines()
        .filter_map(|line| {
            let rest = line.trim_start().strip_prefix("const ")?;
            let (name, rest) = rest.split_once(':')?;
            let (_, value) = rest.split_once('=')?;
            let value = value.trim().strip_suffix(';')?.trim();
            let value = value.trim_end_matches('u').trim_end_matches('i');
            Some((name.trim().to_string(), value.parse().ok()?))
        })
        .collect()
}

#[test]
fn every_shader_constant_matches_its_rust_mirror() {
    let mut failures = Vec::new();
    let mut checked = 0;
    for (file, source) in SHADERS {
        for (name, value) in declared(source) {
            if NOT_CHECKED_HERE.contains(&name.as_str()) {
                continue;
            }
            match rust_value(&name) {
                Some(rust) if (rust - value).abs() <= 1e-6 * rust.abs().max(1.0) => checked += 1,
                Some(rust) => failures.push(format!("{file}: {name} = {value}, Rust has {rust}")),
                None => failures.push(format!("{file}: {name} has no Rust mirror registered")),
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
    assert!(
        checked >= 21,
        "parsed only {checked} constants; is the parser still matching?"
    );
}
