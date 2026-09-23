// Shared declarations prepended to every simulation shader.
//
// All per-cell fields live in one `state` buffer and one `scratch` buffer,
// each a flat array<f32> of `n = width * height` cells per field. Offsets
// mirror `gpu::layout` on the Rust side; a pass reads `state`, writes the
// matching `scratch` region, and the host copies it back, so no pass ever
// reads its own output (the CPU reference's double-buffer discipline).

struct Params {
    width: u32,
    height: u32,
    pigment_count: u32,
    n: u32,
    slope_gain: f32,
    pressure_gain: f32,
    viscosity: f32,
    drag: f32,
    max_velocity: f32,
    blur_radius: u32,
    flow_outward_eta: f32,
    pigment_diffusion: f32,
    water_diffusion: f32,
    diffusion_depth: f32,
    deposition_rate: f32,
    lift_rate: f32,
    wet_lo: f32,
    wet_hi: f32,
    settle_base: f32,
    dry_deposition: f32,
    settle_curve: f32,
    capillary_absorb: f32,
    capillary_epsilon: f32,
    capillary_sigma: f32,
    capillary_rate: f32,
    capillary_dry: f32,
    wet_capillary_dry: f32,
    capillary_seep: f32,
    evaporation: f32,
    dry_threshold: f32,
    mask_evaporation: f32,
    dt: f32,
    max_water_depth: f32,
    max_suspended: f32,
    wet_threshold: f32,
    _pad: f32,
    wet_settle: f32,
    stain_bite: f32,
    carry: f32,
    lift_still: f32,
    lift_flow_gain: f32,
    max_deposited: f32,
    _pad_deposit0: f32,
    _pad_deposit1: f32,
};

struct Stroke {
    kind: u32,
    pigment: u32,
    kick_x: f32,
    kick_y: f32,
    concentration: f32,
    water: f32,
    strength: f32,
    splat_out: f32,
};

struct PigmentCoef {
    density: f32,
    staining_power: f32,
    granulation: f32,
    _pad: f32,
};

// Mirrors sim::DRAIN_* and paint::STROKE_WATER_PAPER_GAIN.
const DRAIN_DEPTH: f32 = 0.5;
const DRAIN_MIN: f32 = 0.15;
const DRAIN_MAX: f32 = 2.0;
const STROKE_WATER_PAPER_GAIN: f32 = 0.5;
// Mirrors sim::CARRY_MIN and sim::CARRY_REACH.
const CARRY_MIN: f32 = 0.1;
const CARRY_REACH: f32 = 1.2;

fn stroke_water_factor(h: f32) -> f32 {
    return max(1.0 + STROKE_WATER_PAPER_GAIN * (0.5 - h) * 2.0, 0.0);
}

@group(0) @binding(0) var<uniform> P: Params;
@group(0) @binding(1) var<storage, read_write> state: array<f32>;
@group(0) @binding(2) var<storage, read_write> scratch: array<f32>;
@group(0) @binding(3) var<storage, read> stamp: array<f32>;
@group(0) @binding(4) var<uniform> stroke: Stroke;
@group(0) @binding(5) var<storage, read> pigments: array<PigmentCoef>;

// state layout
fn o_wet() -> u32 { return 0u; }
fn o_u() -> u32 { return P.n; }
fn o_v() -> u32 { return 2u * P.n; }
fn o_p() -> u32 { return 3u * P.n; }
fn o_s() -> u32 { return 4u * P.n; }
fn o_c() -> u32 { return 5u * P.n; }
fn o_h() -> u32 { return 6u * P.n; }
fn o_m() -> u32 { return 7u * P.n; }
fn o_g(k: u32) -> u32 { return (8u + k) * P.n; }
fn o_d(k: u32) -> u32 { return (8u + P.pigment_count + k) * P.n; }
fn o_dry_rate() -> u32 { return (8u + 2u * P.pigment_count) * P.n; }
fn o_settle_share() -> u32 { return o_dry_rate() + 3u; }

// scratch layout
fn so_u() -> u32 { return 0u; }
fn so_v() -> u32 { return P.n; }
fn so_div() -> u32 { return 2u * P.n; }
fn so_q() -> u32 { return 3u * P.n; }
fn so_q2() -> u32 { return 4u * P.n; }
fn so_blur_tmp() -> u32 { return 5u * P.n; }
fn so_blurred() -> u32 { return 6u * P.n; }
fn so_p() -> u32 { return 7u * P.n; }
fn so_s() -> u32 { return 8u * P.n; }
fn so_g(k: u32) -> u32 { return (9u + k) * P.n; }

// Neighbour indices (left, right, up, down), clamped at the border to the
// cell itself, exactly as the CPU `neighbours` helper does.
fn neighbours(i: u32) -> vec4<u32> {
    let w = P.width;
    let h = P.height;
    let x = i % w;
    let y = i / w;
    let l = select(i, i - 1u, x > 0u);
    let r = select(i, i + 1u, x + 1u < w);
    let up = select(i, i - w, y > 0u);
    let dn = select(i, i + w, y + 1u < h);
    return vec4<u32>(l, r, up, dn);
}

fn wet(i: u32) -> f32 { return state[o_wet() + i]; }

fn bound_velocity(i: u32, u_in: f32, v_in: f32) -> vec2<f32> {
    if wet(i) == 0.0 {
        return vec2<f32>(0.0, 0.0);
    }
    let nb = neighbours(i);
    var u = clamp(u_in, -P.max_velocity, P.max_velocity);
    var v = clamp(v_in, -P.max_velocity, P.max_velocity);
    if u > 0.0 && (wet(nb.y) == 0.0 || nb.y == i) { u = 0.0; }
    if u < 0.0 && (wet(nb.x) == 0.0 || nb.x == i) { u = 0.0; }
    if v > 0.0 && (wet(nb.w) == 0.0 || nb.w == i) { v = 0.0; }
    if v < 0.0 && (wet(nb.z) == 0.0 || nb.z == i) { v = 0.0; }
    return vec2<f32>(u, v);
}
