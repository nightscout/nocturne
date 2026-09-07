/** Grid size of the flow field. Cells are in normalised container space, so the grid stretches with the hero. */
export const FLOW_W = 128;
export const FLOW_H = 80;
/** Speed (normalised container widths or heights per second) that maps to the texture's full range. */
export const FLOW_VMAX = 1.0;

const MAX_DT = 0.05; // s: cap so a hidden tab does not fling the field on return
const STIR_RADIUS = 3.5; // cells: gaussian splat radius under the pointer
const STIR_GAIN = 0.3; // fraction of the pointer's velocity the water takes on
const HEAT_PER_STROKE = 5; // heat per normalised unit of pointer travel at the splat centre
const POKE_HEAT = 0.35; // heat from a stationary press
const HEAT_MAX = 1.25; // headroom above the texture's 1.0 so red holds for a moment before fading
const VELOCITY_HALF_LIFE = 1.0; // s
const HEAT_HALF_LIFE = 2.6; // s
const DIFFUSION = 0.08; // per 1/60 s: share of a cell blended from its four neighbours

const N = FLOW_W * FLOW_H;

/**
 * A stirrable fluid the hero aurora floats on. Pointer strokes inject velocity
 * and heat; each frame both are carried along by the velocity field, spread,
 * and decay. The canvas reads `texture` (R,G velocity, B heat) to bend and
 * colour the aurora, and the chip pool reads `velocityAt` to ride the currents.
 * Velocity has no pressure solve: strokes leave momentum that drifts and dies,
 * which is all the effect needs.
 */
export class FlowField {
  private vx = new Float32Array(N);
  private vy = new Float32Array(N);
  private heat = new Float32Array(N);
  private sx = new Float32Array(N);
  private sy = new Float32Array(N);
  private sh = new Float32Array(N);
  readonly texture = new Uint8Array(N * 4);
  private last = -1;

  constructor() {
    this.pack();
  }

  /**
   * A pointer moved `du, dv` (normalised container units) over `dt` seconds to
   * arrive at `u, v`. The water under it takes on some of that velocity and
   * heats in proportion to the distance paddled.
   */
  stir(u: number, v: number, du: number, dv: number, dt: number): void {
    if (dt <= 0) return;
    const pvx = (du / dt) * STIR_GAIN;
    const pvy = (dv / dt) * STIR_GAIN;
    const heat = Math.hypot(du, dv) * HEAT_PER_STROKE;
    this.splat(u, v, (k, w) => {
      this.vx[k] += (pvx - this.vx[k]) * w;
      this.vy[k] += (pvy - this.vy[k]) * w;
      this.heat[k] = Math.min(HEAT_MAX, this.heat[k] + heat * w);
    });
  }

  /** A press without movement: a little heat, no current. */
  poke(u: number, v: number): void {
    this.splat(u, v, (k, w) => {
      this.heat[k] = Math.min(HEAT_MAX, this.heat[k] + POKE_HEAT * w);
    });
  }

  private splat(u: number, v: number, apply: (k: number, w: number) => void): void {
    const ci = u * FLOW_W - 0.5;
    const cj = v * FLOW_H - 0.5;
    const i0 = Math.max(0, Math.floor(ci - STIR_RADIUS));
    const i1 = Math.min(FLOW_W - 1, Math.ceil(ci + STIR_RADIUS));
    const j0 = Math.max(0, Math.floor(cj - STIR_RADIUS));
    const j1 = Math.min(FLOW_H - 1, Math.ceil(cj + STIR_RADIUS));
    const sigma2 = (STIR_RADIUS * STIR_RADIUS) / 4;
    for (let j = j0; j <= j1; j++) {
      for (let i = i0; i <= i1; i++) {
        const dx = i - ci;
        const dy = j - cj;
        const w = Math.exp(-(dx * dx + dy * dy) / (2 * sigma2));
        if (w > 0.01) apply(j * FLOW_W + i, w);
      }
    }
  }

  /** Step the simulation up to time `t` (seconds). Safe to call from several loops per frame; only the first advances. */
  advance(t: number): void {
    if (this.last < 0) {
      this.last = t;
      return;
    }
    const dt = Math.min(t - this.last, MAX_DT);
    if (dt <= 0) return;
    this.last = t;

    this.advect(dt);
    this.diffuseAndDecay(dt);
    this.pack();
  }

  /** Velocity at a normalised container position, in normalised units per second (DOM axes). */
  velocityAt(u: number, v: number): { vx: number; vy: number } {
    return { vx: this.sample(this.vx, u * FLOW_W - 0.5, v * FLOW_H - 0.5), vy: this.sample(this.vy, u * FLOW_W - 0.5, v * FLOW_H - 0.5) };
  }

  private sample(field: Float32Array, x: number, y: number): number {
    const cx = Math.min(FLOW_W - 1.001, Math.max(0, x));
    const cy = Math.min(FLOW_H - 1.001, Math.max(0, y));
    const i = Math.floor(cx);
    const j = Math.floor(cy);
    const fx = cx - i;
    const fy = cy - j;
    const k = j * FLOW_W + i;
    const a = field[k];
    const b = field[k + 1];
    const c = field[k + FLOW_W];
    const d = field[k + FLOW_W + 1];
    return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
  }

  // Semi-Lagrangian: each cell takes the value that was upstream of it dt ago.
  private advect(dt: number): void {
    const { vx, vy, heat, sx, sy, sh } = this;
    for (let j = 0; j < FLOW_H; j++) {
      for (let i = 0; i < FLOW_W; i++) {
        const k = j * FLOW_W + i;
        const x = i - vx[k] * FLOW_W * dt;
        const y = j - vy[k] * FLOW_H * dt;
        sx[k] = this.sample(vx, x, y);
        sy[k] = this.sample(vy, x, y);
        sh[k] = this.sample(heat, x, y);
      }
    }
    this.vx = sx;
    this.vy = sy;
    this.heat = sh;
    this.sx = vx;
    this.sy = vy;
    this.sh = heat;
  }

  private diffuseAndDecay(dt: number): void {
    const { vx, vy, heat, sx, sy, sh } = this;
    const mixIn = Math.min(0.5, DIFFUSION * dt * 60);
    const keep = 1 - mixIn;
    const vDecay = Math.pow(0.5, dt / VELOCITY_HALF_LIFE);
    const hDecay = Math.pow(0.5, dt / HEAT_HALF_LIFE);
    for (let j = 0; j < FLOW_H; j++) {
      const up = j > 0 ? -FLOW_W : 0;
      const down = j < FLOW_H - 1 ? FLOW_W : 0;
      for (let i = 0; i < FLOW_W; i++) {
        const k = j * FLOW_W + i;
        const left = i > 0 ? -1 : 0;
        const right = i < FLOW_W - 1 ? 1 : 0;
        const blur = (f: Float32Array) =>
          f[k] * keep + ((f[k + up] + f[k + down] + f[k + left] + f[k + right]) / 4) * mixIn;
        sx[k] = blur(vx) * vDecay;
        sy[k] = blur(vy) * vDecay;
        sh[k] = blur(heat) * hDecay;
      }
    }
    this.vx = sx;
    this.vy = sy;
    this.heat = sh;
    this.sx = vx;
    this.sy = vy;
    this.sh = heat;
  }

  private pack(): void {
    const { vx, vy, heat, texture } = this;
    const scale = 127.5 / FLOW_VMAX;
    for (let k = 0; k < N; k++) {
      const o = k * 4;
      texture[o] = Math.max(0, Math.min(255, 127.5 + vx[k] * scale));
      texture[o + 1] = Math.max(0, Math.min(255, 127.5 + vy[k] * scale));
      texture[o + 2] = Math.max(0, Math.min(255, heat[k] * 255));
      texture[o + 3] = 255;
    }
  }
}
