// Rule: optics::render. Cubic B-spline reconstruction of the sim grid at each
// output pixel (4x4 taps, see `optics`' module doc), granulation from the
// paper height sampled at output resolution, one mixed Kubelka-Munk layer per
// pixel and the premultiplied-alpha conversion documented in `optics`, in the
// composite mode stored in the state header (`optics::CompositeMode::flag`,
// packed by `StateLayout` right after `dry_rate`). The state layout is the
// simulation's (see common.wgsl). No deviation from the CPU reference beyond
// f32 transcendental precision; the taps and their order match
// `optics::cubic_sample`. The paper buffer is band-limited at generation time
// (host side, `PaperField::generate_with_pixel_scale`), so the shader samples
// the pre-attenuated height like the CPU reference does.
//
// `render` writes the premultiplied linear frame to `out` for readback;
// `fs_render` shades the same pixel straight into a swapchain texture of the
// output size, so presenting needs no frame buffer and no copy.

struct RenderParams {
    out_width: u32,
    out_height: u32,
    sim_width: u32,
    sim_height: u32,
    pigment_count: u32,
    n: u32,
    // First output row of this dispatch; the host renders large frames as
    // row bands (`GpuEngine::render_frame`).
    y_offset: u32,
    // `fs_render` only: see `encode_for_canvas`.
    encode_srgb: u32,
    granulation_gain: f32,
    wet_pigment_visibility: f32,
    thickness_scale: f32,
    wet_darken: f32,
    wet_sheen_add: f32,
    sheen_depth: f32,
    wet_scatter_loss: f32,
    wet_absorb_gain: f32,
    optical_gamma: f32,
    optical_max: f32,
    optical_mid: f32,
    surface_k1: f32,
    surface_k2: f32,
    surface_coverage_gain: f32,
    _p2: f32,
    _p3: f32,
    crop: vec4<f32>,
};

// Three vec4 per pigment: K rgb, S rgb, (granulation, 0, 0, 0).
@group(0) @binding(0) var<uniform> R: RenderParams;
@group(0) @binding(1) var<storage, read> state: array<f32>;
@group(0) @binding(2) var<storage, read> optics: array<vec4<f32>>;
@group(0) @binding(3) var<storage, read> paper_out: array<f32>;
@group(0) @binding(4) var<storage, read_write> out: array<vec4<f32>>;
// Per pigment, per tap position (see `presence_index`): the tap's presence
// and how far inside the paint it is, written by `presence_taps`.
@group(0) @binding(5) var<storage, read> presence: array<vec2<f32>>;
@group(1) @binding(0) var<storage, read_write> presence_out: array<vec2<f32>>;

const MAX_BETA: f32 = 40.0;
// Mirror optics::ALPHA_SOFTNESS and the LUMINOUS_* tuning constants.
const ALPHA_SOFTNESS: f32 = 0.6;
const LUMINOUS_ALPHA_TOE: f32 = 0.06;
const LUMINOUS_ALPHA_HALF: f32 = 0.4;
const LUMINOUS_ALPHA_MAX: f32 = 0.9;
const LUMINOUS_ALPHA_GRAIN: f32 = 0.7;
const LUMINOUS_COLOUR_FLOOR: f32 = 0.4;
const LUMINOUS_COLOUR_CEILING: f32 = 1.2;
const LUMINOUS_GRAIN_STRENGTH: f32 = 0.7;
const LUMINOUS_CHROMA_GAIN: f32 = 1.2;
const LUMINOUS_PALE_LIFT: f32 = 1.5;
// Mirror optics::LUMINOUS_EDGE_LO, LUMINOUS_EDGE_HI, LUMINOUS_MASK_THICKNESS,
// MASK_MAJORITY and MASK_THIN.
const LUMINOUS_EDGE_LO: f32 = 0.2;
const LUMINOUS_EDGE_HI: f32 = 0.8;
const LUMINOUS_MASK_THICKNESS: f32 = 0.02;
const MASK_MAJORITY: u32 = 5u;
const MASK_THIN: u32 = 2u;
const MAX_PIGMENTS: u32 = 8u;

fn o_p() -> u32 { return 3u * R.n; }
fn o_g(k: u32) -> u32 { return (8u + k) * R.n; }
fn o_d(k: u32) -> u32 { return (8u + R.pigment_count + k) * R.n; }
fn o_composite_mode() -> u32 { return (8u + 2u * R.pigment_count) * R.n + 1u; }

fn km_layer(kx_in: f32, sx_in: f32) -> vec2<f32> {
    let kx = max(kx_in, 0.0);
    let sx = max(sx_in, 0.0);
    if kx + sx <= 0.0 {
        return vec2<f32>(0.0, 1.0);
    }
    let beta = min(sqrt(kx * kx + 2.0 * kx * sx), MAX_BETA);
    var a_over_b = 1.0;
    if beta > 1e-12 {
        a_over_b = (kx + sx) / beta;
    }
    var sinh_over_beta = 1.0 + beta * beta / 6.0;
    if beta >= 1e-4 {
        sinh_over_beta = sinh(beta) / beta;
    }
    let denom = a_over_b * sinh(beta) + cosh(beta);
    let r = sx * sinh_over_beta / denom;
    let t = 1.0 / denom;
    return vec2<f32>(clamp(r, 0.0, 1.0), clamp(t, 0.0, 1.0));
}

// optics::RenderParams::optical: optics::optical_thickness of
// `x * thickness_scale`.
fn optical(x_in: f32) -> f32 {
    let x = x_in * R.thickness_scale;
    if x <= 0.0 {
        return 0.0;
    }
    let g = pow(x, R.optical_gamma);
    return R.optical_max * g / (g + R.optical_mid);
}

// optics::Surface::factor: the gated Saunderson ratio R' / R per channel.
fn surface_factor(w: vec3<f32>, thickness: f32) -> vec3<f32> {
    let gate = clamp(thickness * R.surface_coverage_gain, 0.0, 1.0);
    let gain = (1.0 - R.surface_k1) * (1.0 - R.surface_k2);
    let q = gain / max(vec3<f32>(1.0) - R.surface_k2 * clamp(w, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(1e-6));
    return vec3<f32>(1.0) + (q - vec3<f32>(1.0)) * gate;
}

// optics::luminous_colour.
fn luminous_colour(kx: vec3<f32>, sx: vec3<f32>, scale: f32, thickness: f32, ground: f32, sheen: f32) -> vec3<f32> {
    let lr = km_layer(kx.x * scale, sx.x * scale);
    let lg = km_layer(kx.y * scale, sx.y * scale);
    let lb = km_layer(kx.z * scale, sx.z * scale);
    let r = vec3<f32>(lr.x, lg.x, lb.x);
    let t = vec3<f32>(lr.y, lg.y, lb.y);
    let over = clamp(r + t * t * ground / max(vec3<f32>(1.0) - r * ground, vec3<f32>(1e-6)), vec3<f32>(0.0), vec3<f32>(1.0));
    let w = clamp(over * surface_factor(over, thickness) + vec3<f32>(sheen), vec3<f32>(0.0), vec3<f32>(1.0));
    let mean = (w.x + w.y + w.z) / 3.0;
    return clamp(vec3<f32>(mean) + (w - vec3<f32>(mean)) * LUMINOUS_CHROMA_GAIN, vec3<f32>(0.0), vec3<f32>(1.0));
}

// Mirrors optics::PRESENCE_KERNEL: a Gaussian `2^(-d^2)` over the squared cell
// distance from the tap, normalised to sum to 1. The shift keeps the weights
// exact in f32, so no `exp` and no divergence from the reference table.
fn presence_weight(dx: u32, dy: u32) -> f32 {
    let ox = i32(dx) - 1i;
    let oy = i32(dy) - 1i;
    return 0.25 / f32(1u << u32(ox * ox + oy * oy));
}

// Cubic B-spline weights for a fractional `t` in 0..1; mirrors
// optics::cubic_weights. Non-negative and summing to 1, so the reconstruction
// is a convex combination and never overshoots.
fn cubic_weights(t: f32) -> vec4<f32> {
    let u = 1.0 - t;
    return vec4<f32>(
        u * u * u / 6.0,
        (3.0 * t * t * t - 6.0 * t * t + 4.0) / 6.0,
        (-3.0 * t * t * t + 3.0 * t * t + 3.0 * t + 1.0) / 6.0,
        t * t * t / 6.0,
    );
}

fn painted_amount(k: u32, x: i32, y: i32) -> f32 {
    let cx = u32(min(max(x, 0), i32(R.sim_width) - 1i));
    let cy = u32(min(max(y, 0), i32(R.sim_height) - 1i));
    let j = cy * R.sim_width + cx;
    return max(state[o_d(k) + j] + state[o_g(k) + j] * R.wet_pigment_visibility, 0.0);
}

// A pixel's taps sit at sim cells `x0 - 1 ..= x0 + 2` with `x0` in
// `0..width`, so every tap is one of `(width + 3) * (height + 3)` positions,
// offset by one; a tap outside the grid clamps its 3x3 cell by cell, as
// `optics::cubic_sample`'s window does, so it is not its clamped cell's tap.
fn presence_index(k: u32, px: u32, py: u32) -> u32 {
    let pw = R.sim_width + 3u;
    return (k * (R.sim_height + 3u) + py) * pw + px;
}

// optics::cubic_sample's per-tap presence (the tap cell raised toward its
// 3x3 by a Gaussian-weighted fourth-power mean) and inside share, which
// depend on the tap alone: computed once per tap position instead of once
// per output pixel that reads it.
@compute @workgroup_size(256)
fn presence_taps(@builtin(global_invocation_id) gid: vec3<u32>) {
    let pw = R.sim_width + 3u;
    let ph = R.sim_height + 3u;
    let c = gid.x;
    if c >= pw * ph { return; }
    let px = c % pw;
    let py = c / pw;
    let tx = i32(px) - 1i;
    let ty = i32(py) - 1i;
    for (var k = 0u; k < R.pigment_count; k++) {
        var acc = 0.0;
        var neighbours = 0u;
        var soft = 0.0;
        for (var dy = 0u; dy < 3u; dy++) {
            for (var dx = 0u; dx < 3u; dx++) {
                let q = painted_amount(k, tx + i32(dx) - 1i, ty + i32(dy) - 1i);
                let q2 = q * q;
                acc += presence_weight(dx, dy) * q2 * q2;
                if q > LUMINOUS_MASK_THICKNESS {
                    soft += presence_weight(dx, dy);
                    if dy != 1u || dx != 1u {
                        neighbours += 1u;
                    }
                }
            }
        }
        let own = painted_amount(k, tx, ty);
        // optics::cubic_sample: the painted share of the tap's 3x3, full
        // when a painted majority rings it or when the tap is a painted thin
        // mark.
        var inside = soft;
        let thin = own > LUMINOUS_MASK_THICKNESS && neighbours <= MASK_THIN;
        if neighbours >= MASK_MAJORITY || thin {
            inside = 1.0;
        }
        presence_out[presence_index(k, px, py)] = vec2<f32>(max(sqrt(sqrt(acc)), own), inside);
    }
}

// The premultiplied linear RGBA of output pixel (x, y).
fn shade(x: u32, y: u32) -> vec4<f32> {
    let u = (f32(x) + 0.5) / f32(R.out_width) * R.crop.z + R.crop.x;
    let v = (f32(y) + 0.5) / f32(R.out_height) * R.crop.w + R.crop.y;
    let w = R.sim_width;
    let h = R.sim_height;
    let fx = clamp(u * f32(w) - 0.5, 0.0, f32(w - 1u));
    let fy = clamp(v * f32(h) - 0.5, 0.0, f32(h - 1u));
    let x0 = u32(floor(fx));
    let y0 = u32(floor(fy));
    let tx = fx - f32(x0);
    let ty = fy - f32(y0);
    let wx = cubic_weights(tx);
    let wy = cubic_weights(ty);

    var depth_s = 0.0;
    var dep: array<f32, MAX_PIGMENTS>;
    var sus: array<f32, MAX_PIGMENTS>;
    var pres: array<f32, MAX_PIGMENTS>;
    for (var k = 0u; k < MAX_PIGMENTS; k++) { dep[k] = 0.0; sus[k] = 0.0; pres[k] = 0.0; }
    for (var oy = 0u; oy < 4u; oy++) {
        let tap_y = i32(y0) + i32(oy) - 1i;
        let cy = min(max(tap_y, 0), i32(h) - 1i);
        let wyi = wy[oy];
        for (var ox = 0u; ox < 4u; ox++) {
            let tap_x = i32(x0) + i32(ox) - 1i;
            let cx = min(max(tap_x, 0), i32(w) - 1i);
            let wgt = wx[ox] * wyi;
            let i = u32(cy) * w + u32(cx);
            depth_s += state[o_p() + i] * wgt;
            for (var k = 0u; k < R.pigment_count; k++) {
                dep[k] += state[o_d(k) + i] * wgt;
                sus[k] += state[o_g(k) + i] * wgt;
            }
        }
    }
    var mask = 0.0;
    for (var k = 0u; k < R.pigment_count; k++) {
        var mask_k = 0.0;
        for (var oy = 0u; oy < 4u; oy++) {
            for (var ox = 0u; ox < 4u; ox++) {
                let wgt = wx[ox] * wy[oy];
                let tap = presence[presence_index(k, x0 + ox, y0 + oy)];
                pres[k] += tap.x * wgt;
                mask_k += tap.y * wgt;
            }
        }
        mask = max(mask, mask_k);
    }
    let h_out = paper_out[y * R.out_width + x];
    let wet_look = clamp(depth_s / R.sheen_depth, 0.0, 1.0);
    let ground = 1.0 - R.wet_darken * wet_look;
    let sheen = R.wet_sheen_add * wet_look;
    // optics::WetLook::enrich, applied to the colour mix only.
    let wet_k = 1.0 + R.wet_absorb_gain * wet_look;
    let wet_sc = 1.0 - R.wet_scatter_loss * wet_look;
    var kx = vec3<f32>(0.0);
    var sx = vec3<f32>(0.0);
    var thickness = 0.0;
    var kx_plain = vec3<f32>(0.0);
    var sx_plain = vec3<f32>(0.0);
    var plain_thickness = 0.0;
    // optics::render: the curve saturates the total amount and each pigment
    // takes its share.
    var amount = 0.0;
    var amount_pres = 0.0;
    for (var k = 0u; k < R.pigment_count; k++) {
        amount += max(dep[k] + sus[k] * R.wet_pigment_visibility, 0.0);
        amount_pres += max(pres[k], 0.0);
    }
    let share = optical(amount) / max(amount, 1e-6);
    let pres_thickness = optical(amount_pres);
    for (var k = 0u; k < R.pigment_count; k++) {
        let base = max(dep[k] + sus[k] * R.wet_pigment_visibility, 0.0);
        let gran = optics[k * 3u + 2u].x;
        let grain = 1.0 + gran * R.granulation_gain * (0.5 - h_out) * 2.0;
        let plain = base * share;
        let t = max(plain * max(grain, 0.0), 0.0);
        plain_thickness += plain;
        kx_plain += optics[k * 3u].xyz * plain;
        sx_plain += optics[k * 3u + 1u].xyz * plain;
        thickness += t;
        kx += optics[k * 3u].xyz * t;
        sx += optics[k * 3u + 1u].xyz * t;
    }
    // optics::to_premultiplied_with_coverage: coverage from the dry grained
    // mix, its returned light scaled by the surface factor.
    let cr = km_layer(kx.x, sx.x);
    let cg = km_layer(kx.y, sx.y);
    let cb = km_layer(kx.z, sx.z);
    let r_cov = vec3<f32>(cr.x, cg.x, cb.x);
    let t_cov = vec3<f32>(cr.y, cg.y, cb.y);
    let ret_raw = clamp(t_cov * t_cov / max(vec3<f32>(1.0) - r_cov, vec3<f32>(1e-6)), vec3<f32>(0.0), vec3<f32>(1.0));
    let white_cov = clamp(r_cov + ret_raw, vec3<f32>(0.0), vec3<f32>(1.0));
    let ret_cov = ret_raw * surface_factor(white_cov, thickness);
    let min_cov = min(ret_cov.x, min(ret_cov.y, ret_cov.z));
    let mean_cov = (ret_cov.x + ret_cov.y + ret_cov.z) / 3.0;
    let passed = clamp(min_cov + (mean_cov - min_cov) * ALPHA_SOFTNESS, 0.0, 1.0);
    // Colour from the enriched mix over the film-darkened ground, surface
    // corrected, plus the sheen.
    let lr = km_layer(kx.x * wet_k, sx.x * wet_sc);
    let lg = km_layer(kx.y * wet_k, sx.y * wet_sc);
    let lb = km_layer(kx.z * wet_k, sx.z * wet_sc);
    let r = vec3<f32>(lr.x, lg.x, lb.x);
    let t = vec3<f32>(lr.y, lg.y, lb.y);
    let over_ground = clamp(r + t * t * ground / max(vec3<f32>(1.0) - r * ground, vec3<f32>(1e-6)), vec3<f32>(0.0), vec3<f32>(1.0));
    let w_ground = clamp(over_ground * surface_factor(over_ground, thickness) + vec3<f32>(sheen), vec3<f32>(0.0), vec3<f32>(1.0));
    var rgb = clamp(w_ground - vec3<f32>(passed), vec3<f32>(0.0), vec3<f32>(1.0));
    var out_alpha = 1.0 - passed;
    if state[o_composite_mode()] > 0.5 {
        // optics::luminous_alpha: presence-driven alpha carrying the tooth.
        let body = LUMINOUS_ALPHA_MAX * (1.0 - exp2(-pres_thickness / LUMINOUS_ALPHA_HALF));
        let grain_ratio = thickness / max(plain_thickness, 1e-6);
        let tooth = max(1.0 + (grain_ratio - 1.0) * LUMINOUS_ALPHA_GRAIN, 0.0);
        var alpha_l = clamp(body * tooth, 0.0, 1.0)
            * smoothstep(0.0, LUMINOUS_ALPHA_TOE, pres_thickness)
            * smoothstep(LUMINOUS_EDGE_LO, LUMINOUS_EDGE_HI, mask);
        // optics::to_premultiplied_luminous_tuned: colour from the damped
        // granulated mix, its thickness raised to the presence or the floor
        // and capped at the ceiling.
        let reference = max(plain_thickness, 1e-6);
        let scale = min(max(max(LUMINOUS_COLOUR_FLOOR, pres_thickness) / reference, 1.0), LUMINOUS_COLOUR_CEILING / reference);
        let kx_d = kx_plain + (kx - kx_plain) * LUMINOUS_GRAIN_STRENGTH;
        let sx_d = sx_plain + (sx - sx_plain) * LUMINOUS_GRAIN_STRENGTH;
        let colour_t = plain_thickness * scale;
        let w_glow = luminous_colour(kx_d * wet_k, sx_d * wet_sc, scale, colour_t, ground, sheen);
        // The pale lift reads the dry colour, so the film never moves alpha.
        let w_dry = luminous_colour(kx_d, sx_d, scale, colour_t, 1.0, 0.0);
        let luminance = 0.2126 * w_dry.x + 0.7152 * w_dry.y + 0.0722 * w_dry.z;
        let clear = 1.0 - alpha_l;
        if clear > 0.0 {
            alpha_l = 1.0 - pow(clear, 1.0 + LUMINOUS_PALE_LIFT * luminance);
        }
        out_alpha = alpha_l;
        rgb = w_glow * alpha_l;
    }
    return vec4<f32>(rgb, out_alpha);
}

@compute @workgroup_size(16, 16)
fn render(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = gid.x;
    let y = gid.y + R.y_offset;
    if x >= R.out_width || y >= R.out_height { return; }
    out[y * R.out_width + x] = shade(x, y);
}

struct VertexOut {
    @builtin(position) position: vec4<f32>,
};

@vertex
fn vs_fullscreen(@builtin(vertex_index) index: u32) -> VertexOut {
    // Three vertices covering clip space: (-1,-1), (3,-1), (-1,3).
    let x = f32(i32(index & 1u) * 4 - 1);
    let y = f32(i32(index >> 1u) * 4 - 1);
    var vert: VertexOut;
    vert.position = vec4<f32>(x, y, 0.0, 1.0);
    return vert;
}

@fragment
fn fs_render(in: VertexOut) -> @location(0) vec4<f32> {
    let x = min(u32(in.position.x), R.out_width - 1u);
    let y = min(u32(in.position.y), R.out_height - 1u);
    return encode_for_canvas(shade(x, y), R.encode_srgb);
}
