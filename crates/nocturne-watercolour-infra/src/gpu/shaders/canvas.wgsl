fn linear_to_srgb(v: vec3<f32>) -> vec3<f32> {
    let c = clamp(v, vec3<f32>(0.0), vec3<f32>(1.0));
    let low = c * 12.92;
    let high = 1.055 * pow(c, vec3<f32>(1.0 / 2.4)) - 0.055;
    return select(high, low, c <= vec3<f32>(0.0031308));
}

// Browser canvases composite encoded sRGB with premultiplied alpha.
fn encode_for_canvas(px: vec4<f32>, encode_srgb: u32) -> vec4<f32> {
    let alpha = clamp(px.a, 0.0, 1.0);
    if encode_srgb == 0u {
        return vec4<f32>(clamp(px.rgb, vec3<f32>(0.0), vec3<f32>(alpha)), alpha);
    }
    if alpha <= 1.0 / 1024.0 { return vec4<f32>(0.0); }
    let straight = clamp(px.rgb / alpha, vec3<f32>(0.0), vec3<f32>(1.0));
    return vec4<f32>(linear_to_srgb(straight) * alpha, alpha);
}
