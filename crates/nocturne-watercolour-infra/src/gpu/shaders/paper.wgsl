// Render-resolution paper height: `PaperField::generate_with_pixel_scale` in
// `domain::paper`, one invocation per output pixel, bit-identical to the CPU.
//
// Exactness rests on three things. The lattice hash is integer splitmix64 on
// `vec2<u32>` halves, so it is exact. Add, subtract and multiply are
// correctly rounded in WGSL, but a driver may contract `a * b + c` into a
// fused multiply-add the CPU does not do, or reassociate a chain of products,
// and a trailing `* 1.0` from a uniform does not stop every driver. Every
// product that feeds a sum or a further product therefore goes through
// `fenced`, an integer round trip no compiler can see through, so each
// operation sees the rounded value the CPU sees. Division is only 2.5 ULP in
// WGSL, so the three divisions go through `div_rn`, an integer long division
// rounded to nearest even. Every per-field constant is computed on the CPU
// (`PaperTerms`) and uploaded, so none is re-derived here.

struct PaperParams {
    // Lattice seeds as (lo, hi) pairs: grain 0..3, pool a, pool b, fibre.
    seeds: array<vec4<u32>, 4>,
    // width, height, first pixel of this dispatch, pixel count of the field.
    dims: vec4<u32>,
    // 1/width, 1/height, isotropic x scale, isotropic y scale.
    geom: vec4<f32>,
    // Per noise term (grain 0..3, pool a, pool b, fibre): its lattice
    // coordinate is `(u or u * base) * scale + offset` on each axis.
    scale_x: array<vec4<f32>, 2>,
    scale_y: array<vec4<f32>, 2>,
    offset_x: array<vec4<f32>, 2>,
    offset_y: array<vec4<f32>, 2>,
    grain_amp: vec4<f32>,
    grain_band: vec4<f32>,
    // base, full weight, fibre band, unused.
    terms: vec4<f32>,
    // 1 - POOL_WEIGHT, POOL_WEIGHT, 1 - fibre weight, fibre weight.
    mix_weights: vec4<f32>,
    // height amplitude, unused x3.
    height: vec4<f32>,
    // Zero; see `fenced`.
    fence: vec4<u32>,
}

@group(0) @binding(0) var<uniform> P: PaperParams;
@group(0) @binding(1) var<storage, read_write> paper_out: array<f32>;

fn seed_at(i: u32) -> vec2<u32> {
    let pair = P.seeds[i / 2u];
    if (i % 2u) == 0u {
        return pair.xy;
    }
    return pair.zw;
}

// Full 64-bit product of two u32s as (lo, hi).
fn mul32_wide(a: u32, b: u32) -> vec2<u32> {
    let a0 = a & 0xFFFFu;
    let a1 = a >> 16u;
    let b0 = b & 0xFFFFu;
    let b1 = b >> 16u;
    let p00 = a0 * b0;
    let p01 = a0 * b1;
    let p10 = a1 * b0;
    let mid = (p00 >> 16u) + (p01 & 0xFFFFu) + (p10 & 0xFFFFu);
    return vec2<u32>(a * b, a1 * b1 + (p01 >> 16u) + (p10 >> 16u) + (mid >> 16u));
}

// Low 64 bits of a 64 x 64 product.
fn mul64(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    let lo = mul32_wide(a.x, b.x);
    return vec2<u32>(lo.x, lo.y + a.x * b.y + a.y * b.x);
}

fn add64(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    let lo = a.x + b.x;
    return vec2<u32>(lo, a.y + b.y + select(0u, 1u, lo < a.x));
}

// `z ^ (z >> s)` for 0 < s < 32.
fn xorshift64(z: vec2<u32>, s: u32) -> vec2<u32> {
    return z ^ vec2<u32>((z.x >> s) | (z.y << (32u - s)), z.y >> s);
}

// `seed::mix64`.
fn mix64(k: vec2<u32>) -> vec2<u32> {
    var z = add64(k, vec2<u32>(0x7F4A7C15u, 0x9E3779B9u));
    z = mul64(xorshift64(z, 30u), vec2<u32>(0x1CE4E5B9u, 0xBF58476Du));
    z = mul64(xorshift64(z, 27u), vec2<u32>(0x133111EBu, 0x94D049BBu));
    return xorshift64(z, 31u);
}

// `seed::hash2`.
fn hash2(seed: vec2<u32>, x: i32, y: i32) -> f32 {
    let k = seed ^ vec2<u32>(0u, u32(x)) ^ mul32_wide(u32(y), 0x9E3779B9u);
    return f32(mix64(k).y >> 8u) * (1.0 / 16777216.0);
}

fn smooth_step01(t: f32) -> f32 {
    return fenced(t * t) * (3.0 - 2.0 * t);
}

// `p`, rounded: the xor with a uniform zero keeps the product from being
// fused into the sum it feeds.
fn fenced(p: f32) -> f32 {
    return bitcast<f32>(bitcast<u32>(p) ^ P.fence.x);
}

fn lerp_exact(a: f32, b: f32, t: f32) -> f32 {
    return a + fenced((b - a) * t);
}

fn value_noise(seed: vec2<u32>, x: f32, y: f32) -> f32 {
    let xf = floor(x);
    let yf = floor(y);
    let fx = smooth_step01(x - xf);
    let fy = smooth_step01(y - yf);
    let xi = i32(xf);
    let yi = i32(yf);
    // One hash call site keeps the shader small; FXC compiles every inlined
    // copy, and engine start-up waits for it.
    var corner: array<f32, 4>;
    for (var c = 0u; c < 4u; c = c + 1u) {
        corner[c] = hash2(seed, xi + i32(c & 1u), yi + i32(c >> 1u));
    }
    let top = lerp_exact(corner[0], corner[1], fx);
    let bottom = lerp_exact(corner[2], corner[3], fx);
    return lerp_exact(top, bottom, fy);
}

// `a / b` rounded to nearest even, for normal `b` and a normal quotient.
fn div_rn(a: f32, b: f32) -> f32 {
    if a == 0.0 {
        return a;
    }
    let ab = bitcast<u32>(a);
    let bb = bitcast<u32>(b);
    var ea = i32((ab >> 23u) & 0xFFu);
    let eb = i32((bb >> 23u) & 0xFFu);
    var ma = (ab & 0x7FFFFFu) | 0x800000u;
    let mb = (bb & 0x7FFFFFu) | 0x800000u;
    if ma < mb {
        ma = ma << 1u;
        ea = ea - 1;
    }
    // 25 quotient bits: the leading one, 23 fraction bits and a round bit.
    var rem = ma;
    var q = 0u;
    for (var i = 0u; i < 25u; i = i + 1u) {
        q = q << 1u;
        if rem >= mb {
            rem = rem - mb;
            q = q | 1u;
        }
        rem = rem << 1u;
    }
    var mant = q >> 1u;
    if (q & 1u) == 1u && (rem != 0u || (mant & 1u) == 1u) {
        mant = mant + 1u;
    }
    var e = ea - eb + 127;
    if mant == 0x1000000u {
        mant = mant >> 1u;
        e = e + 1;
    }
    return bitcast<f32>(((ab ^ bb) & 0x80000000u) | (u32(e) << 23u) | (mant & 0x7FFFFFu));
}

@compute @workgroup_size(256)
fn generate_paper(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = P.dims.z + gid.x;
    if i >= P.dims.w {
        return;
    }
    let width = P.dims.x;
    let u = fenced((f32(i % width) + 0.5) * P.geom.x) * P.geom.z;
    let v = fenced((f32(i / width) + 0.5) * P.geom.y) * P.geom.w;
    let ub = fenced(u * P.terms.x);
    let vb = fenced(v * P.terms.x);
    var noise: array<f32, 7>;
    for (var t = 0u; t < 7u; t = t + 1u) {
        let grain = t < 4u;
        let x = fenced(select(ub, u, grain) * P.scale_x[t / 4u][t % 4u]) + P.offset_x[t / 4u][t % 4u];
        let y = fenced(select(vb, v, grain) * P.scale_y[t / 4u][t % 4u]) + P.offset_y[t / 4u][t % 4u];
        noise[t] = value_noise(seed_at(t), x, y);
    }

    var deviation = 0.0;
    for (var octave = 0u; octave < 4u; octave = octave + 1u) {
        let weight = fenced(P.grain_amp[octave] * P.grain_band[octave]);
        deviation = deviation + fenced(weight * (noise[octave] - 0.5));
    }
    let grain = 0.5 + div_rn(deviation, P.terms.y);
    let pool = div_rn(noise[4] + fenced(0.7 * noise[5]), 1.7);
    let fibre = 0.5 + fenced((noise[6] - 0.5) * P.terms.z);
    let body = fenced(grain * P.mix_weights.x) + fenced(pool * P.mix_weights.y);
    let mixed = fenced(body * P.mix_weights.z) + fenced(fibre * P.mix_weights.w);
    paper_out[i] = 0.5 + fenced((mixed - 0.5) * P.height.x);
}
