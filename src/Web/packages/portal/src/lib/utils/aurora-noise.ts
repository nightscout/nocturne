// aurora-noise.ts
// JS port of the GLSL noise functions from AuroraCanvas.svelte.
// Kept in sync with the shader so chips respond to the same wave field.

function fract(x: number): number {
  return x - Math.floor(x);
}

function hash(px: number, py: number): number {
  // fract(p * vec2(123.34, 456.21))
  px = fract(px * 123.34);
  py = fract(py * 456.21);
  // p += dot(p, p + 45.32)
  const d = px * (px + 45.32) + py * (py + 45.32);
  px += d;
  py += d;
  return fract(px * py);
}

function noise(px: number, py: number): number {
  const ix = Math.floor(px), iy = Math.floor(py);
  const fx = px - ix, fy = py - iy;
  const a = hash(ix,     iy    );
  const b = hash(ix + 1, iy    );
  const c = hash(ix,     iy + 1);
  const d = hash(ix + 1, iy + 1);
  // Hermite smoothing (matches GLSL: u = f*f*(3-2*f))
  const ux = fx * fx * (3 - 2 * fx);
  const uy = fy * fy * (3 - 2 * fy);
  // mix(mix(a,b,ux), mix(c,d,ux), uy)
  return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
}

function fbm(px: number, py: number): number {
  let v = 0, a = 0.5;
  for (let i = 0; i < 5; i++) {
    v += a * noise(px, py);
    px *= 2.03;
    py *= 2.03;
    a  *= 0.5;
  }
  return v;
}

/**
 * Sample the aurora shader's domain-warp flow vector at a chip position.
 *
 * @param cx  chip center x in container pixels
 * @param cy  chip center y in container pixels
 * @param cw  container width in pixels
 * @param ch  container height in pixels
 * @param t   time in seconds — pass `now / 1000 * speed` to match AuroraCanvas exactly (speed defaults to 1.0)
 * @returns   { rx, ry } — approximately in [0, 1]. Subtract 0.5 and scale for force.
 */
export function sampleFlow(
  cx: number, cy: number,
  cw: number, ch: number,
  t: number,
): { rx: number; ry: number } {
  // Map container-px → shader p-space: centered, normalised by height.
  // Matches: vec2 p = (gl_FragCoord.xy - 0.5*u_res) / u_res.y
  const px = (cx - cw * 0.5) / ch;
  const py = (cy - ch * 0.5) / ch;

  // Matches shader: float t = u_t * 0.06
  const st = t * 0.06;

  // q — first warp layer (matches shader exactly)
  const qx = fbm(px * 1.4,       py * 1.4 + st         );
  const qy = fbm(px * 1.4 + 5.2, py * 1.4 - st * 0.8   );

  // r — second warp layer (this is the flow vector we use as force)
  const rx = fbm(px * 2.1 + 1.8 * qx + 1.7 + st * 1.3, py * 2.1 + 1.8 * qy + 9.2      );
  const ry = fbm(px * 2.1 + 1.8 * qx + 8.3 - st * 1.1, py * 2.1 + 1.8 * qy + 2.8      );

  return { rx, ry };
}
