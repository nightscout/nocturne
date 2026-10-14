// Rule: Curtis UpdateVelocities (sim::pass_velocity).
// Velocity from paper slope and water-depth gradient, explicit viscosity
// Laplacian, drag, clamp, and zeroing of components that point into dry
// cells. Deviation from Curtis: collocated velocities, as in the CPU
// reference. No deviation from the CPU reference.

fn velocity_at(i: u32) -> vec2<f32> {
    let nb = neighbours(i);
    let l = nb.x; let r = nb.y; let up = nb.z; let dn = nb.w;
    let gx = (state[o_h() + r] - state[o_h() + l]) * 0.5 * P.slope_gain
        + (state[o_p() + r] - state[o_p() + l]) * 0.5 * P.pressure_gain;
    let gy = (state[o_h() + dn] - state[o_h() + up]) * 0.5 * P.slope_gain
        + (state[o_p() + dn] - state[o_p() + up]) * 0.5 * P.pressure_gain;
    let u = state[o_u() + i];
    let v = state[o_v() + i];
    let lap_u = state[o_u() + l] + state[o_u() + r] + state[o_u() + up] + state[o_u() + dn] - 4.0 * u;
    let lap_v = state[o_v() + l] + state[o_v() + r] + state[o_v() + up] + state[o_v() + dn] - 4.0 * v;
    let nu = (u - P.dt * gx + P.viscosity * lap_u) * (1.0 - P.drag);
    let nv = (v - P.dt * gy + P.viscosity * lap_v) * (1.0 - P.drag);
    return bound_velocity(i, nu, nv);
}

// velocity and divergence (`pressure.wgsl`'s rule) in one dispatch. A workgroup covers a 16x16 tile:
// it evaluates the velocity over the tile and one cell around it into
// workgroup memory, by the one code path for every cell, then writes the
// tile's velocity and its divergence (divergence's own arithmetic) from
// there.
var<workgroup> tile_u: array<f32, 324>;
var<workgroup> tile_v: array<f32, 324>;

fn tile_index(j: u32, x0: i32, y0: i32) -> u32 {
    let w = P.width;
    return u32(i32(j / w) - y0) * 18u + u32(i32(j % w) - x0);
}

@compute @workgroup_size(256)
fn velocity_divergence(
    @builtin(workgroup_id) wg: vec3<u32>,
    @builtin(local_invocation_index) lid: u32,
) {
    let w = i32(P.width);
    let h = i32(P.height);
    let x0 = i32(wg.x * 16u) - 1;
    let y0 = i32(wg.y * 16u) - 1;
    for (var c = lid; c < 324u; c += 256u) {
        let x = x0 + i32(c % 18u);
        let y = y0 + i32(c / 18u);
        if x >= 0 && y >= 0 && x < w && y < h {
            let b = velocity_at(u32(y * w + x));
            tile_u[c] = b.x;
            tile_v[c] = b.y;
        }
    }
    workgroupBarrier();
    let x = x0 + 1 + i32(lid % 16u);
    let y = y0 + 1 + i32(lid / 16u);
    if x >= w || y >= h { return; }
    let i = u32(y * w + x);
    let own = tile_index(i, x0, y0);
    scratch[so_u() + i] = tile_u[own];
    scratch[so_v() + i] = tile_v[own];
    let nb = neighbours(i);
    var d = 0.0;
    if wet(i) > 0.0 {
        d = 0.5 * (tile_u[tile_index(nb.y, x0, y0)] - tile_u[tile_index(nb.x, x0, y0)]
            + tile_v[tile_index(nb.w, x0, y0)] - tile_v[tile_index(nb.z, x0, y0)]);
    }
    scratch[so_div() + i] = d;
    scratch[so_q() + i] = 0.0;
}
