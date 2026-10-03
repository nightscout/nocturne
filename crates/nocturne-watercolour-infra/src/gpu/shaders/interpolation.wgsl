@group(0) @binding(0) var previous: texture_2d<f32>;
@group(0) @binding(1) var current: texture_2d<f32>;
@group(0) @binding(2) var<uniform> blend: vec4<f32>;

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    return vec4<f32>(f32(i32(index & 1u) * 4 - 1), f32(i32(index >> 1u) * 4 - 1), 0.0, 1.0);
}

@fragment
fn fragment(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let at = vec2<i32>(position.xy);
    let px = mix(textureLoad(previous, at, 0), textureLoad(current, at, 0), blend.x);
    let alpha = clamp(px.a, 0.0, 1.0);
    if blend.y == 0.0 { return vec4<f32>(clamp(px.rgb, vec3<f32>(0.0), vec3<f32>(alpha)), alpha); }
    if alpha <= 1.0 / 1024.0 { return vec4<f32>(0.0); }
    let straight = clamp(px.rgb / alpha, vec3<f32>(0.0), vec3<f32>(1.0));
    let low = straight * 12.92;
    let high = 1.055 * pow(straight, vec3<f32>(1.0 / 2.4)) - 0.055;
    return vec4<f32>(select(high, low, straight <= vec3<f32>(0.0031308)) * alpha, alpha);
}
