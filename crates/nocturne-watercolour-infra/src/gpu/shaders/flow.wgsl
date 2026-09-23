// Rules: Curtis FlowOutward and MovePigment (sim::pass_blur_h, pass_blur_v,
// pass_advect), then the standing-water swirl (sim::pass_swirl_gate_h/v,
// pass_swirl, domain::swirl) and the tick clock. Separable box blur of the wet mask, then one gather pass
// that advects water and suspended pigment upwind, diffuses them between wet
// neighbours in proportion to depth, and removes water near the wet
// boundary (edge darkening, scaled by local depth and paper height).
// Deviations from Curtis are those of the CPU reference (water advected,
// pigment diffusion, depth- and height-scaled drain). No deviation from the
// CPU reference.

@compute @workgroup_size(256)
fn blur_h(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let w = i32(P.width);
    let radius = i32(P.blur_radius);
    let inv = 1.0 / f32(2 * radius + 1);
    let x = i32(i) % w;
    let row = i32(i) - x;
    var sum = 0.0;
    for (var dx = -radius; dx <= radius; dx++) {
        let sx = clamp(x + dx, 0, w - 1);
        sum += state[o_wet() + u32(row + sx)];
    }
    scratch[so_blur_tmp() + i] = sum * inv;
}

@compute @workgroup_size(256)
fn blur_v(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let w = i32(P.width);
    let h = i32(P.height);
    let radius = i32(P.blur_radius);
    let inv = 1.0 / f32(2 * radius + 1);
    let x = i32(i) % w;
    let y = i32(i) / w;
    var sum = 0.0;
    for (var dy = -radius; dy <= radius; dy++) {
        let sy = clamp(y + dy, 0, h - 1);
        sum += scratch[so_blur_tmp() + u32(sy * w + x)];
    }
    scratch[so_blurred() + i] = sum * inv;
}

fn diffusion_weight(i: u32, j: u32, coef: f32, pi: f32) -> f32 {
    if j == i || wet(j) == 0.0 {
        return 0.0;
    }
    return coef * 0.25 * min((pi + state[o_p() + j]) * 0.5 / P.diffusion_depth, 1.0);
}

@compute @workgroup_size(256)
fn advect(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let k_count = P.pigment_count;
    if wet(i) == 0.0 {
        scratch[so_p() + i] = state[o_p() + i];
        for (var k = 0u; k < k_count; k++) {
            scratch[so_g(k) + i] = state[o_g(k) + i];
        }
        return;
    }
    let nb = neighbours(i);
    let l = nb.x; let r = nb.y; let up = nb.z; let dn = nb.w;
    let ui = state[o_u() + i];
    let vi = state[o_v() + i];
    let keep = 1.0 - (abs(ui) + abs(vi)) * P.dt;
    let in_l = select(0.0, max(state[o_u() + l], 0.0) * P.dt, l != i);
    let in_r = select(0.0, max(-state[o_u() + r], 0.0) * P.dt, r != i);
    let in_u = select(0.0, max(state[o_v() + up], 0.0) * P.dt, up != i);
    let in_d = select(0.0, max(-state[o_v() + dn], 0.0) * P.dt, dn != i);
    let pi = state[o_p() + i];
    var wl = diffusion_weight(i, l, P.pigment_diffusion, pi);
    var wr = diffusion_weight(i, r, P.pigment_diffusion, pi);
    var wu = diffusion_weight(i, up, P.pigment_diffusion, pi);
    var wd = diffusion_weight(i, dn, P.pigment_diffusion, pi);
    for (var k = 0u; k < k_count; k++) {
        let base = o_g(k);
        let gi = state[base + i];
        var ng = gi * keep + in_l * state[base + l] + in_r * state[base + r] + in_u * state[base + up] + in_d * state[base + dn];
        ng += wl * (state[base + l] - gi) + wr * (state[base + r] - gi) + wu * (state[base + up] - gi) + wd * (state[base + dn] - gi);
        scratch[so_g(k) + i] = clamp(ng, 0.0, P.max_suspended);
    }
    wl = diffusion_weight(i, l, P.water_diffusion, pi);
    wr = diffusion_weight(i, r, P.water_diffusion, pi);
    wu = diffusion_weight(i, up, P.water_diffusion, pi);
    wd = diffusion_weight(i, dn, P.water_diffusion, pi);
    let pl = state[o_p() + l];
    let pr = state[o_p() + r];
    let pu = state[o_p() + up];
    let pd = state[o_p() + dn];
    var np = pi * keep + in_l * pl + in_r * pr + in_u * pu + in_d * pd;
    np += wl * (pl - pi) + wr * (pr - pi) + wu * (pu - pi) + wd * (pd - pi);
    let drain = clamp(pi / DRAIN_DEPTH, DRAIN_MIN, DRAIN_MAX) * (1.5 - state[o_h() + i]);
    np -= P.flow_outward_eta * (1.0 - scratch[so_blurred() + i]) * drain * P.dt;
    scratch[so_p() + i] = clamp(np, 0.0, P.max_water_depth);
}

// Mirrors swirl::SWIRL_DRIFT_SKEW, sim::SWIRL_FACE_LIMIT and
// sim::SWIRL_SUBSTEPS.
const SWIRL_DRIFT_SKEW: f32 = 0.618;
const SWIRL_FACE_LIMIT: f32 = 0.25;
const SWIRL_SUBSTEPS: u32 = 5u;

fn swirl_lattice(x: i32, y: i32, seed: u32) -> f32 {
    var h = (bitcast<u32>(x) * 0x8DA6B343u) ^ (bitcast<u32>(y) * 0xD8163841u) ^ (seed * 0xCB1AB31Fu);
    h = h ^ (h >> 16u);
    h = h * 0x7FEB352Du;
    h = h ^ (h >> 15u);
    h = h * 0x846CA68Bu;
    h = h ^ (h >> 16u);
    return f32(h >> 8u) * (1.0 / 16777216.0);
}

fn swirl_value_noise(qx: f32, qy: f32, seed: u32) -> f32 {
    let fx = floor(qx);
    let fy = floor(qy);
    let ix = i32(fx);
    let iy = i32(fy);
    let tx = qx - fx;
    let ty = qy - fy;
    let sx = tx * tx * (3.0 - 2.0 * tx);
    let sy = ty * ty * (3.0 - 2.0 * ty);
    let a = swirl_lattice(ix, iy, seed);
    let b = swirl_lattice(ix + 1, iy, seed);
    let c = swirl_lattice(ix, iy + 1, seed);
    let d = swirl_lattice(ix + 1, iy + 1, seed);
    return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
}

fn swirl_stream(cx: u32, cy: u32) -> f32 {
    let aspect = state[o_aspect()];
    let ax = max(aspect, 1.0);
    let ay = max(1.0 / aspect, 1.0);
    let size = f32(P.width);
    let seed = u32(state[o_swirl_seed()]);
    let px = f32(cx) / size * ax * P.swirl_frequency;
    let py = f32(cy) / size * ay * P.swirl_frequency;
    let t = state[o_tick()] * P.swirl_drift;
    let n = swirl_value_noise(px + t, py + t * SWIRL_DRIFT_SKEW, seed);
    let scale = P.swirl_speed / f32(SWIRL_SUBSTEPS) * size / (P.swirl_frequency * ax * ay);
    return scale * (n - 0.5);
}

// sim::pass_swirl_gate_h: the raw gate blurred along x, off-grid cells dry.
@compute @workgroup_size(256)
fn swirl_gate_h(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let w = i32(P.width);
    let r = i32(P.swirl_radius_x);
    let inv = 1.0 / f32(2 * r + 1);
    let x = i32(i) % w;
    let row = i32(i) - x;
    var sum = 0.0;
    for (var dx = -r; dx <= r; dx++) {
        let sx = x + dx;
        if sx >= 0 && sx < w {
            let j = u32(row + sx);
            if wet(j) != 0.0 {
                sum += smoothstep(P.wet_lo, P.swirl_depth, state[o_p() + j]);
            }
        }
    }
    scratch[so_div() + i] = sum * inv;
}

// sim::pass_swirl_gate_v.
@compute @workgroup_size(256)
fn swirl_gate_v(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let w = i32(P.width);
    let h = i32(P.height);
    let r = i32(P.swirl_radius_y);
    let inv = 1.0 / f32(2 * r + 1);
    let x = i32(i) % w;
    let y = i32(i) / w;
    var sum = 0.0;
    for (var dy = -r; dy <= r; dy++) {
        let sy = y + dy;
        if sy >= 0 && sy < h {
            sum += scratch[so_div() + u32(sy * w + x)];
        }
    }
    scratch[so_q() + i] = sum * inv;
}

fn swirl_taper(x: i32, y: i32) -> f32 {
    if x < 0 || y < 0 || x >= i32(P.width) || y >= i32(P.height) {
        return 0.0;
    }
    let j = u32(y) * P.width + u32(x);
    if wet(j) == 0.0 {
        return 0.0;
    }
    return smoothstep(0.5, 1.0, scratch[so_q() + j]);
}

fn swirl_corner(cx: u32, cy: u32) -> f32 {
    let x = i32(cx);
    let y = i32(cy);
    let taper = min(min(swirl_taper(x - 1, y - 1), swirl_taper(x, y - 1)), min(swirl_taper(x - 1, y), swirl_taper(x, y)));
    if taper == 0.0 {
        return 0.0;
    }
    return taper * swirl_stream(cx, cy);
}

@compute @workgroup_size(256)
fn swirl(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    var s = vec4<f32>(0.0);
    if P.swirl_speed > 0.0 && wet(i) != 0.0 {
        let x = i % P.width;
        let y = i / P.width;
        let tl = swirl_corner(x, y);
        let tr = swirl_corner(x + 1u, y);
        let bl = swirl_corner(x, y + 1u);
        let br = swirl_corner(x + 1u, y + 1u);
        s = clamp(vec4<f32>(bl - tl, br - tr, -(tr - tl), -(br - bl)), vec4<f32>(-SWIRL_FACE_LIMIT), vec4<f32>(SWIRL_FACE_LIMIT));
    }
    let nb = neighbours(i);
    let keep = 1.0 - (max(s.y, 0.0) + max(-s.x, 0.0) + max(s.w, 0.0) + max(-s.z, 0.0)) * P.dt;
    let in_l = max(s.x, 0.0) * P.dt;
    let in_r = max(-s.y, 0.0) * P.dt;
    let in_u = max(s.z, 0.0) * P.dt;
    let in_d = max(-s.w, 0.0) * P.dt;
    for (var k = 0u; k < P.pigment_count; k++) {
        let base = o_g(k);
        let ng = state[base + i] * keep + in_l * state[base + nb.x] + in_r * state[base + nb.y] + in_u * state[base + nb.z] + in_d * state[base + nb.w];
        scratch[so_g(k) + i] = clamp(ng, 0.0, P.max_suspended);
    }
}

// sim::step's closing `grid.tick += 1`.
@compute @workgroup_size(1)
fn clock() {
    state[o_tick()] = state[o_tick()] + 1.0;
}
