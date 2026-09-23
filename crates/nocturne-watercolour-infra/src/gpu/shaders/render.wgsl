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
    _p1: u32,
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
};

// Three vec4 per pigment: K rgb, S rgb, (granulation, 0, 0, 0).
@group(0) @binding(0) var<uniform> R: RenderParams;
@group(0) @binding(1) var<storage, read> state: array<f32>;
@group(0) @binding(2) var<storage, read> optics: array<vec4<f32>>;
@group(0) @binding(3) var<storage, read> paper_out: array<f32>;
@group(0) @binding(4) var<storage, read_write> out: array<vec4<f32>>;

const MAX_BETA: f32 = 40.0;
// Mirror optics::ALPHA_SOFTNESS, LUMINOUS_ALPHA_TOE and LUMINOUS_ALPHA_FULL.
const ALPHA_SOFTNESS: f32 = 0.6;
const LUMINOUS_ALPHA_TOE: f32 = 0.03;
const LUMINOUS_ALPHA_FULL: f32 = 0.2;
// Mirror optics::LUMINOUS_COLOUR_FLOOR and LUMINOUS_COLOUR_CEILING.
const LUMINOUS_COLOUR_FLOOR: f32 = 0.5;
const LUMINOUS_COLOUR_CEILING: f32 = 1.6;
// Mirrors optics::LUMINOUS_GRAIN_STRENGTH.
const LUMINOUS_GRAIN_STRENGTH: f32 = 0.38;
// Mirror optics::LUMINOUS_EDGE_LO, LUMINOUS_EDGE_HI, LUMINOUS_MASK_THICKNESS,
// MASK_MAJORITY and MASK_THIN.
const LUMINOUS_EDGE_LO: f32 = 0.2;
const LUMINOUS_EDGE_HI: f32 = 0.8;
const LUMINOUS_MASK_THICKNESS: f32 = 0.02;
const MASK_MAJORITY: u32 = 5u;
const MASK_THIN: u32 = 2u;
const MAX_PIGMENTS: u32 = 8u;

fn o_wet() -> u32 { return 0u; }
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

@compute @workgroup_size(16, 16)
fn render(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = gid.x;
    let y = gid.y + R.y_offset;
    if x >= R.out_width || y >= R.out_height { return; }
    let u = (f32(x) + 0.5) / f32(R.out_width);
    let v = (f32(y) + 0.5) / f32(R.out_height);
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
    // optics::cubic_sample's window: the 6x6 cells the taps and their 3x3
    // neighbourhoods read, clamped at the grid edge, read once per pigment.
    // From it, per tap, the presence (the tap cell raised toward its 3x3 by a
    // Gaussian-weighted fourth-power mean) and how far inside the paint the
    // tap is.
    var mask = 0.0;
    var win: array<f32, 36>;
    for (var k = 0u; k < R.pigment_count; k++) {
        for (var wy_i = 0u; wy_i < 6u; wy_i++) {
            let ny = u32(min(max(i32(y0) + i32(wy_i) - 2i, 0), i32(h) - 1i));
            for (var wx_i = 0u; wx_i < 6u; wx_i++) {
                let nx = u32(min(max(i32(x0) + i32(wx_i) - 2i, 0), i32(w) - 1i));
                let j = ny * w + nx;
                win[wy_i * 6u + wx_i] = max(state[o_d(k) + j] + state[o_g(k) + j] * R.wet_pigment_visibility, 0.0);
            }
        }
        var mask_k = 0.0;
        for (var oy = 0u; oy < 4u; oy++) {
            for (var ox = 0u; ox < 4u; ox++) {
                let wgt = wx[ox] * wy[oy];
                var acc = 0.0;
                var neighbours = 0u;
                var soft = 0.0;
                for (var dy = 0u; dy < 3u; dy++) {
                    for (var dx = 0u; dx < 3u; dx++) {
                        let q = win[(oy + dy) * 6u + ox + dx];
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
                let own = win[(oy + 1u) * 6u + ox + 1u];
                pres[k] += max(sqrt(sqrt(acc)), own) * wgt;
                // optics::cubic_sample: the painted share of the tap's 3x3,
                // full when a painted majority rings it or when the tap is a
                // painted thin mark.
                var inside = soft;
                let thin = own > LUMINOUS_MASK_THICKNESS && neighbours <= MASK_THIN;
                if neighbours >= MASK_MAJORITY || thin {
                    inside = 1.0;
                }
                mask_k += inside * wgt;
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
    var kx_pres = vec3<f32>(0.0);
    var sx_pres = vec3<f32>(0.0);
    var pres_thickness = 0.0;
    for (var k = 0u; k < R.pigment_count; k++) {
        let base = dep[k] + sus[k] * R.wet_pigment_visibility;
        let gran = optics[k * 3u + 2u].x;
        let grain = 1.0 + gran * R.granulation_gain * (0.5 - h_out) * 2.0;
        let plain = optical(max(base, 0.0));
        let t = max(plain * max(grain, 0.0), 0.0);
        plain_thickness += plain;
        kx_plain += optics[k * 3u].xyz * plain;
        sx_plain += optics[k * 3u + 1u].xyz * plain;
        let presence = optical(max(pres[k], 0.0));
        pres_thickness += presence;
        kx_pres += optics[k * 3u].xyz * presence;
        sx_pres += optics[k * 3u + 1u].xyz * presence;
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
        // optics::to_premultiplied_luminous_tuned: alpha from the dilated
        // plain presence's coverage, colour from the damped granulated mix
        // brought to no less than the colour floor of the plain thickness.
        let pr = km_layer(kx_pres.x, sx_pres.x);
        let pg = km_layer(kx_pres.y, sx_pres.y);
        let pb = km_layer(kx_pres.z, sx_pres.z);
        let r_plain = vec3<f32>(pr.x, pg.x, pb.x);
        let t_plain = vec3<f32>(pr.y, pg.y, pb.y);
        let ret_plain = clamp(t_plain * t_plain / max(vec3<f32>(1.0) - r_plain, vec3<f32>(1e-6)), vec3<f32>(0.0), vec3<f32>(1.0));
        let min_plain = min(ret_plain.x, min(ret_plain.y, ret_plain.z));
        let mean_plain = (ret_plain.x + ret_plain.y + ret_plain.z) / 3.0;
        let coverage = clamp(1.0 - (min_plain + (mean_plain - min_plain) * ALPHA_SOFTNESS), 0.0, 1.0);
        out_alpha = smoothstep(LUMINOUS_ALPHA_TOE, LUMINOUS_ALPHA_FULL, coverage)
            * smoothstep(LUMINOUS_EDGE_LO, LUMINOUS_EDGE_HI, mask);
        let reference = max(plain_thickness, 1e-6);
        let scale = min(max(max(LUMINOUS_COLOUR_FLOOR, pres_thickness) / reference, 1.0), LUMINOUS_COLOUR_CEILING / reference);
        // optics::damp_texture: grain deviation damped toward the plain mix,
        // then enriched by the film.
        let kx_d = (kx_plain + (kx - kx_plain) * LUMINOUS_GRAIN_STRENGTH) * wet_k;
        let sx_d = (sx_plain + (sx - sx_plain) * LUMINOUS_GRAIN_STRENGTH) * wet_sc;
        let rr = km_layer(kx_d.x * scale, sx_d.x * scale);
        let rg = km_layer(kx_d.y * scale, sx_d.y * scale);
        let rb = km_layer(kx_d.z * scale, sx_d.z * scale);
        let r_ref = vec3<f32>(rr.x, rg.x, rb.x);
        let t_ref = vec3<f32>(rr.y, rg.y, rb.y);
        let over_ground_ref = clamp(r_ref + t_ref * t_ref * ground / max(vec3<f32>(1.0) - r_ref * ground, vec3<f32>(1e-6)), vec3<f32>(0.0), vec3<f32>(1.0));
        let w_ref = clamp(over_ground_ref * surface_factor(over_ground_ref, plain_thickness * scale) + vec3<f32>(sheen), vec3<f32>(0.0), vec3<f32>(1.0));
        rgb = w_ref * out_alpha;
    }
    out[y * R.out_width + x] = vec4<f32>(rgb, out_alpha);
}
