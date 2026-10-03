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
    return encode_for_canvas(px, u32(blend.y));
}
