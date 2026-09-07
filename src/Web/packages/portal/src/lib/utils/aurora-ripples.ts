import { toAuroraSpace } from "./aurora-noise";

/** Uniform array size in the AuroraCanvas shader; the oldest ripple is dropped past this. */
export const MAX_RIPPLES = 8;
/** Seconds: a ring has faded to nothing by this age. */
export const RIPPLE_LIFE = 2.6;
/** p-units/s. p is height-normalised, so this is roughly 460 px/s on a 920 px hero. */
export const RIPPLE_SPEED = 0.5;
/** p-units: half-width of the crest. */
export const RIPPLE_WIDTH = 0.06;

interface Ripple {
  /** Origin in the shader's p-space (centred, y up, normalised by height). */
  x: number;
  y: number;
  /** auroraTime() at the drop, in seconds. */
  born: number;
}

/**
 * Strength of a ripple's crest at distance `d` (p-units) from its origin,
 * `age` seconds after the drop: a gaussian ring at the wavefront that fades
 * quadratically over the ripple's life. Mirrored by `crest()` in the shader.
 */
function rippleCrest(d: number, age: number): number {
  if (age < 0 || age >= RIPPLE_LIFE) return 0;
  const front = (d - age * RIPPLE_SPEED) / RIPPLE_WIDTH;
  const fade = 1 - age / RIPPLE_LIFE;
  return Math.exp(-front * front) * fade * fade;
}

/**
 * The waves a click drops into the aurora. The canvas draws them and the chips
 * are shoved by them from this one field, so the ring on screen and the push a
 * chip feels are the same wave at the same radius.
 */
export class RippleField {
  private ripples: Ripple[] = [];

  /** Drop a ripple at a container-pixel position. `t` is auroraTime() now. */
  spawn(cx: number, cy: number, cw: number, ch: number, t: number): void {
    if (ch <= 0) return;
    const { px, py } = toAuroraSpace(cx, cy, cw, ch);
    this.ripples.push({ x: px, y: py, born: t });
    if (this.ripples.length > MAX_RIPPLES) this.ripples.shift();
  }

  private live(t: number): readonly Ripple[] {
    if (this.ripples.length && t - this.ripples[0].born >= RIPPLE_LIFE) {
      this.ripples = this.ripples.filter((r) => t - r.born < RIPPLE_LIFE);
    }
    return this.ripples;
  }

  /**
   * Fill `out` (length MAX_RIPPLES * 3) with [x, y, age] per live ripple for
   * the shader's `u_rip` uniform. Returns the live count.
   */
  pack(t: number, out: Float32Array): number {
    const live = this.live(t);
    out.fill(0);
    for (let i = 0; i < live.length; i++) {
      out[i * 3] = live[i].x;
      out[i * 3 + 1] = live[i].y;
      out[i * 3 + 2] = t - live[i].born;
    }
    return live.length;
  }

  /**
   * Outward shove on a floater at a container-pixel position: crest strength
   * under the floater along the unit radial away from each live origin, summed
   * and capped at unit length so stacked ripples cannot fling a chip. The
   * caller scales it into an acceleration. Returned in DOM axes (y down).
   */
  shove(cx: number, cy: number, cw: number, ch: number, t: number): { x: number; y: number } {
    const live = this.live(t);
    let x = 0;
    let y = 0;
    if (live.length === 0) return { x, y };
    const { px, py } = toAuroraSpace(cx, cy, cw, ch);
    for (const r of live) {
      const dx = px - r.x;
      const dy = py - r.y;
      const d = Math.hypot(dx, dy);
      if (d < 1e-4) continue;
      const c = rippleCrest(d, t - r.born) / d;
      x += dx * c;
      y -= dy * c; // p-space y is up; the DOM's is down
    }
    const mag = Math.hypot(x, y);
    if (mag > 1) {
      x /= mag;
      y /= mag;
    }
    return { x, y };
  }
}
