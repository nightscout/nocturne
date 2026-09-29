// Rule: Curtis RelaxDivergence (sim::pass_divergence, pass_jacobi,
// pass_project). Divergence of the wet-cell velocity field `velocity` left
// in scratch, a fixed number of Jacobi iterations on the pressure correction
// (ping-pong between the two entry points `jacobi_a` q -> q2 and `jacobi_b`
// q2 -> q), then projection back into state.
// Deviation from Curtis: fixed iteration count instead of a tolerance, same
// as the CPU reference. No deviation from the CPU reference.

@compute @workgroup_size(256)
fn divergence(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if i >= P.n { return; }
    let nb = neighbours(i);
    var d = 0.0;
    if wet(i) > 0.0 {
        d = 0.5 * (scratch[so_u() + nb.y] - scratch[so_u() + nb.x] + scratch[so_v() + nb.w] - scratch[so_v() + nb.z]);
    }
    scratch[so_div() + i] = d;
    scratch[so_q() + i] = 0.0;
}

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
