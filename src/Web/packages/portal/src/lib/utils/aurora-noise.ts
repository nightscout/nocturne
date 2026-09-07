// aurora-noise.ts
// JS port of the GLSL in AuroraCanvas.svelte, so the hero chips can sample the
// exact brightness field the shader is drawing underneath them. Any change to
// the shader's noise chain must be mirrored here or the chips drift out of step.

const CLOCK_ORIGIN_MS = typeof performance !== "undefined" ? performance.now() : 0;

/**
 * Seconds elapsed on the clock shared by the canvas and the pool. Both must
 * read the same origin: the shader's pattern is a function of time, so a
 * different zero on either side samples a different frame of the animation.
 */
export function auroraTime(nowMs: number = performance.now()): number {
  return (nowMs - CLOCK_ORIGIN_MS) / 1000;
}

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

function smoothstep(e0: number, e1: number, x: number): number {
  const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0)));
  return t * t * (3 - 2 * t);
}

/**
 * The shader's pre-palette brightness `v` at a point in its own p-space
 * (centred, y up, normalised by height) at time `t` seconds.
 *
 * Mirrors main() in AuroraCanvas.svelte up to `ramp(v)`: the colour ramp and
 * vignette are monotone or static, so `v` is what moves on screen.
 */
function auroraBrightness(px: number, py: number, t: number): number {
  const st = t * 0.06;

  // q: first warp layer. Time enters y only: vec2(0., t) and vec2(5.2, -t*0.8).
  const qx = fbm(px * 1.4,       py * 1.4 + st      );
  const qy = fbm(px * 1.4 + 5.2, py * 1.4 - st * 0.8);

  // r: second warp layer. `+ t*1.3` and `- t*1.1` are scalars added to a vec2,
  // so they shift both components.
  const wx = px * 2.1 + 1.8 * qx;
  const wy = py * 2.1 + 1.8 * qy;
  const rx = fbm(wx + 1.7 + st * 1.3, wy + 9.2 + st * 1.3);
  const ry = fbm(wx + 8.3 - st * 1.1, wy + 2.8 - st * 1.1);

  const n = fbm(px * 1.6 + 2.2 * rx, py * 1.6 + 2.2 * ry);

  const yb = py * 1.15 + 0.05;
  const band = smoothstep(0, 0.55, 1 - yb * yb);
  return Math.pow(n, 1.15) * (0.55 + 0.6 * band);
}

export interface SurfaceSample {
  /** Brightness in [0, ~1]: 0 is the dark trough, 1 the bright crest. */
  value: number;
  /** Brightness gradient in container pixels (per px). Points uphill, toward the crest. */
  slopeX: number;
  slopeY: number;
  /**
   * Apparent velocity of the pattern under this point in container px/s, from
   * the optical-flow constraint dv/dt + grad(v) . u = 0. This is the direction
   * the crest is visibly travelling, so a floater riding it moves this way.
   */
  flowX: number;
  flowY: number;
}

const D_SPACE = 0.004; // p-space finite-difference step (~4 px at 920 px tall)
const D_TIME = 0.05; // seconds
const FLOW_EPS = 1e-4; // regulariser: flow is undefined where the field is flat

/**
 * Container pixel to the shader's p-space: centred, normalised by height, y up.
 * `cw`/`ch` are the canvas's CSS size. The y-axis flips because gl_FragCoord
 * runs bottom-up while the DOM runs top-down.
 */
export function toAuroraSpace(
  cx: number, cy: number,
  cw: number, ch: number,
): { px: number; py: number } {
  return { px: (cx - cw * 0.5) / ch, py: (ch * 0.5 - cy) / ch };
}

/** Sample the drawn field at a container-pixel position. */
export function sampleSurface(
  cx: number, cy: number,
  cw: number, ch: number,
  t: number,
): SurfaceSample {
  const { px, py } = toAuroraSpace(cx, cy, cw, ch);

  const v0 = auroraBrightness(px, py, t);
  const dvdx = (auroraBrightness(px + D_SPACE, py, t) - v0) / D_SPACE;
  const dvdy = (auroraBrightness(px, py + D_SPACE, t) - v0) / D_SPACE;
  const dvdt = (auroraBrightness(px, py, t + D_TIME) - v0) / D_TIME;

  // Normal flow in p-space units per second, then scale to px/s (px = p * ch).
  const g2 = dvdx * dvdx + dvdy * dvdy + FLOW_EPS;
  const ux = (-dvdt * dvdx) / g2;
  const uy = (-dvdt * dvdy) / g2;

  return {
    value: v0,
    slopeX: dvdx / ch,
    slopeY: -dvdy / ch,
    flowX: ux * ch,
    flowY: -uy * ch,
  };
}
