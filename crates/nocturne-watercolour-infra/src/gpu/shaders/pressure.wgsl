// Rule: Curtis RelaxDivergence (sim::pass_divergence, pass_jacobi,
// pass_project). The divergence of the wet-cell velocity field is taken with
// the velocity itself (`velocity_divergence`, velocity.wgsl); then a fixed
// number of Jacobi iterations on the pressure correction, two per dispatch
// (`jacobi_pair_a` q -> q2, `jacobi_pair_b` q2 -> q; `jacobi_a`/`jacobi_b`
// run the last of an odd count), then projection back into state.
// Deviation from Curtis: fixed iteration count instead of a tolerance, same
// as the CPU reference. No deviation from the CPU reference.

fn jacobi_step(i: u32, q_in: u32, q_out: u32) {
    if wet(i) == 0.0 {
        scratch[q_out + i] = 0.0;
        return;
    }
    let nb = neighbours(i);
    scratch[q_out + i] = 0.25 * (scratch[q_in + nb.x] + scratch[q_in + nb.y] + scratch[q_in + nb.z] + scratch[q_in + nb.w] - scratch[so_div() + i]);
}

@compute @workgroup_size(256)
fn jacobi_a(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    jacobi_step(i, so_q(), so_q2());
}

@compute @workgroup_size(256)
fn jacobi_b(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    jacobi_step(i, so_q2(), so_q());
}

// Two Jacobi iterations per dispatch. A workgroup covers a 16x16 tile: it
// loads `q_in` over the tile and two cells around it, runs the first
// iteration over the tile and one cell around it (the ring its neighbours
// read) into workgroup memory, then the second over the tile into `q_out`.
// A ring cell is computed by each workgroup whose tile it borders, by the
// same instructions, so every tile reads the value the one-iteration pass
// would have written. The arithmetic is `jacobi_step`'s: sums of loads and
// one product, which no compiler can contract differently.
const JACOBI_TILE: u32 = 16u;
const JACOBI_IN: u32 = 20u;
const JACOBI_MID: u32 = 18u;
var<workgroup> jacobi_in: array<f32, 400>;
var<workgroup> jacobi_mid: array<f32, 324>;

// Tile-local index of grid cell `j` in a `side`-wide region whose first
// cell is (x0, y0).
fn jacobi_local(j: u32, x0: i32, y0: i32, side: u32) -> u32 {
    let w = P.width;
    return u32(i32(j / w) - y0) * side + u32(i32(j % w) - x0);
}

fn jacobi_pair(wg: vec3<u32>, lid: u32, q_in: u32, q_out: u32) {
    let w = i32(P.width);
    let h = i32(P.height);
    let x0 = i32(wg.x * JACOBI_TILE) - 2;
    let y0 = i32(wg.y * JACOBI_TILE) - 2;
    for (var c = lid; c < JACOBI_IN * JACOBI_IN; c += 256u) {
        let x = x0 + i32(c % JACOBI_IN);
        let y = y0 + i32(c / JACOBI_IN);
        var q = 0.0;
        if x >= 0 && y >= 0 && x < w && y < h {
            q = scratch[q_in + u32(y * w + x)];
        }
        jacobi_in[c] = q;
    }
    workgroupBarrier();
    for (var c = lid; c < JACOBI_MID * JACOBI_MID; c += 256u) {
        let x = x0 + 1 + i32(c % JACOBI_MID);
        let y = y0 + 1 + i32(c / JACOBI_MID);
        var q = 0.0;
        if x >= 0 && y >= 0 && x < w && y < h {
            let i = u32(y * w + x);
            if wet(i) != 0.0 {
                let nb = neighbours(i);
                q = 0.25 * (jacobi_in[jacobi_local(nb.x, x0, y0, JACOBI_IN)]
                    + jacobi_in[jacobi_local(nb.y, x0, y0, JACOBI_IN)]
                    + jacobi_in[jacobi_local(nb.z, x0, y0, JACOBI_IN)]
                    + jacobi_in[jacobi_local(nb.w, x0, y0, JACOBI_IN)]
                    - scratch[so_div() + i]);
            }
        }
        jacobi_mid[c] = q;
    }
    workgroupBarrier();
    let x = x0 + 2 + i32(lid % JACOBI_TILE);
    let y = y0 + 2 + i32(lid / JACOBI_TILE);
    if x >= w || y >= h { return; }
    let i = u32(y * w + x);
    if wet(i) == 0.0 {
        scratch[q_out + i] = 0.0;
        return;
    }
    let nb = neighbours(i);
    scratch[q_out + i] = 0.25 * (jacobi_mid[jacobi_local(nb.x, x0 + 1, y0 + 1, JACOBI_MID)]
        + jacobi_mid[jacobi_local(nb.y, x0 + 1, y0 + 1, JACOBI_MID)]
        + jacobi_mid[jacobi_local(nb.z, x0 + 1, y0 + 1, JACOBI_MID)]
        + jacobi_mid[jacobi_local(nb.w, x0 + 1, y0 + 1, JACOBI_MID)]
        - scratch[so_div() + i]);
}

@compute @workgroup_size(256)
fn jacobi_pair_a(
    @builtin(workgroup_id) wg: vec3<u32>,
    @builtin(local_invocation_index) lid: u32,
) {
    jacobi_pair(wg, lid, so_q(), so_q2());
}

@compute @workgroup_size(256)
fn jacobi_pair_b(
    @builtin(workgroup_id) wg: vec3<u32>,
    @builtin(local_invocation_index) lid: u32,
) {
    jacobi_pair(wg, lid, so_q2(), so_q());
}

// Reads only its own cell's velocity, so it can write straight into state.
fn project_from(i: u32, q: u32) {
    let nb = neighbours(i);
    let u = scratch[so_u() + i] - 0.5 * (scratch[q + nb.y] - scratch[q + nb.x]);
    let v = scratch[so_v() + i] - 0.5 * (scratch[q + nb.w] - scratch[q + nb.z]);
    let b = bound_velocity(i, u, v);
    state[o_u() + i] = b.x;
    state[o_v() + i] = b.y;
}

// After an even iteration count the correction is in q.
@compute @workgroup_size(256)
fn project(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    project_from(i, so_q());
}

@compute @workgroup_size(256)
fn project_q2(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    project_from(i, so_q2());
}
