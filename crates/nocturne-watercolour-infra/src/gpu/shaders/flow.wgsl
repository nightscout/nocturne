// Rules: Curtis FlowOutward and MovePigment (sim::pass_blur_h, pass_blur_v,
// pass_advect). Separable box blur of the wet mask, then one gather pass
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
