// Operations (paint::apply_brush, apply_water, apply_lift, sim::dry_all).
// The stamp coverage is rasterised on the CPU by the shared `paint` module
// and uploaded, so geometry is identical on both backends; these entry
// points only add it into the grid, with stroke water modulated by paper
// height as in paint::stroke_water_factor. The laydown flow (paint::
// StrokeFlow) arrives already computed in the stroke uniform, and only its
// outward direction is evaluated here, off the same stamp buffer the CPU
// reads. No deviation from the CPU reference.

// Mirror of paint::outward_at. Coverage is highest on the centre line and
// falls to zero at the rim, so the descent direction of the stamp field is
// the direction the landing water is shouldered. The neighbour clamps match
// the Rust saturating/min pair cell for cell; the lockstep tests compare
// intermediate ticks and will catch any drift.
fn outward_at(i: u32) -> vec2<f32> {
    let w = P.width;
    let h = P.height;
    if w == 0u || h == 0u { return vec2<f32>(0.0, 0.0); }
    let x = i % w;
    let y = i / w;
    let l = max(x, 1u) - 1u;
    let r = min(x + 1u, w - 1u);
    let up = max(y, 1u) - 1u;
    let dn = min(y + 1u, h - 1u);
    let gx = stamp[y * w + r] - stamp[y * w + l];
    let gy = stamp[dn * w + x] - stamp[up * w + x];
    let len = sqrt(gx * gx + gy * gy);
    if len <= 1e-6 { return vec2<f32>(0.0, 0.0); }
    return vec2<f32>(-gx / len, -gy / len);
}

fn inject_flow(i: u32, cov: f32) {
    var o = vec2<f32>(0.0, 0.0);
    if stroke.splat_out != 0.0 { o = outward_at(i); }
    state[o_u() + i] += cov * (stroke.kick_x + stroke.splat_out * o.x);
    state[o_v() + i] += cov * (stroke.kick_y + stroke.splat_out * o.y);
}

@compute @workgroup_size(256)
fn apply_brush(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let cov = stamp[i] * state[o_m() + i];
    if cov <= 0.0 { return; }
    let k = min(stroke.pigment, P.pigment_count - 1u);
    let water = stroke.water * cov * stroke_water_factor(state[o_h() + i]);
    state[o_p() + i] += water;
    state[o_g(k) + i] += stroke.concentration * cov;
    if water > P.wet_threshold || state[o_p() + i] > P.wet_threshold {
        state[o_wet() + i] = 1.0;
    }
    inject_flow(i, cov);
}

@compute @workgroup_size(256)
fn apply_water(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let cov = stamp[i] * state[o_m() + i];
    if cov <= 0.0 { return; }
    state[o_p() + i] += stroke.water * cov * stroke_water_factor(state[o_h() + i]);
    if state[o_p() + i] > P.wet_threshold {
        state[o_wet() + i] = 1.0;
    }
    inject_flow(i, cov);
}

@compute @workgroup_size(256)
fn apply_lift(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let f = clamp(1.0 - stroke.strength * stamp[i], 0.0, 1.0);
    if f >= 1.0 { return; }
    state[o_p() + i] *= f;
    for (var k = 0u; k < P.pigment_count; k++) {
        state[o_g(k) + i] *= f;
        state[o_d(k) + i] *= f;
    }
}

@compute @workgroup_size(256)
fn dry_all(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    for (var k = 0u; k < P.pigment_count; k++) {
        let d = state[o_d(k) + i] + state[o_g(k) + i];
        state[o_d(k) + i] = min(d, P.max_deposited);
        state[o_g(k) + i] = 0.0;
    }
    state[o_p() + i] = 0.0;
    state[o_wet() + i] = 0.0;
    state[o_u() + i] = 0.0;
    state[o_v() + i] = 0.0;
    state[o_s() + i] = 0.0;
}
