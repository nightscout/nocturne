import type { PaletteId, Surface } from '../types';
import { DEFAULT_INTENSITY } from '../types';
import type { WasmModule } from './wasm-types';
import { dropSimResolution } from './drop-scene';

export interface BloomSceneOptions {
  palette?: PaletteId;
  surface?: Surface;
  seed?: number;
  /** Normalised trend from -1 (falling) to 1 (rising); defaults to level. */
  slope?: number;
  intensity?: number;
  /** Device pixel ratio the canvas will be backed at; sizes the simulation grid. */
  dpr?: number;
  /**
   * Paints in this sRGB colour (0..1 per channel) as an opaque body colour instead of the
   * palette's base pigment, so the bloom lands as that colour over whatever is under the canvas.
   */
  colour?: readonly [number, number, number];
}

interface SceneDocument {
  palette: { name: string; entries: { role: string; pigment: unknown }[] };
  [key: string]: unknown;
}

/** The catalogue artwork whose paper and palette the bloom borrows; only its timeline is replaced. */
const DONOR = 'avatar-wash';
const TOTAL_TICKS = 220;
/**
 * Splotches dropped into the wet sheet a few ticks apart. Each grows by one small charge a tick for
 * `GROW_TICKS`, from `START_REACH` to `END_REACH` short sides, until they run into one another and
 * cover the canvas. The steps are smaller than a frame's worth of growth, so every edge advances
 * continuously.
 */
const SPLOTCHES = 3;
const SPLOTCH_STAGGER_TICKS = 7;
const GROW_TICKS = 90;
const START_REACH = 0.07;
const END_REACH = 0.95;
/** Pigment each charge carries; the centre gathers every charge and the edge only the last few. */
const CHARGE_CONCENTRATION = 0.06;
/** Largest simulation grid: the bloom's edge is soft, and a smaller grid keeps each tick cheap. */
const MAX_SIM_RESOLUTION = 320;
/**
 * The gum film and the paper texture darken a heavy layer (Saunderson's surface term, see the
 * optics module), so the pigment is mixed lighter by this much in linear light to dry to the
 * colour asked for. Measured against a render of the dried bloom.
 */
const SURFACE_GAIN = 1.5;
/** Scattering per unit thickness of a body colour: high enough to hide what lies under it. */
const BODY_SCATTER = 5;
/** Tick after which the last charge has bled to the corners. */
export const BLOOM_COVERED_TICK = 140;
export const BLOOM_TOTAL_TICKS = TOTAL_TICKS;
const DRY_AT = 0.6;
const DRY_RATE = 3;

const toLinear = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);

/**
 * A Kubelka-Munk body colour whose reflectance at full hiding is `colour`:
 * `R = 1 + K/S - sqrt((K/S)^2 + 2 K/S)`, so `K = S (1 - R)^2 / (2 R)`.
 */
function bodyColour(colour: readonly [number, number, number]) {
  const reflect = colour.map((c) => Math.min(0.97, Math.max(0.002, toLinear(c) * SURFACE_GAIN)));
  return {
    name: 'body_colour',
    k: reflect.map((r) => (BODY_SCATTER * (1 - r) ** 2) / (2 * r)),
    s: reflect.map(() => BODY_SCATTER),
    density: 0.7,
    staining_power: 0.5,
    granulation: 0.9,
  };
}

/** Eases the growth so the bloom races out at first and slows as it reaches the edges. */
const easeOut = (t: number) => 1 - (1 - t) ** 2.2;

function mulberry(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * A charge of pigment dropped into a wetted sheet that blooms outward to cover the whole canvas:
 * the gesture of touching a loaded brush into wet paper. Coordinates run 0..1 over each canvas
 * axis; radii are in units of the canvas's short side.
 */
export function bloomScene(
  module: Pick<WasmModule, 'catalogueScene'>,
  width: number,
  height: number,
  { palette = 'slate', surface = 'light', seed = 0, slope = 0, intensity = DEFAULT_INTENSITY, dpr = 1, colour }: BloomSceneOptions = {},
): string {
  const pxW = Math.max(1, Math.round(width * dpr));
  const pxH = Math.max(1, Math.round(height * dpr));
  const simResolution = Math.min(MAX_SIM_RESOLUTION, dropSimResolution(Math.max(pxW, pxH)));
  const doc = JSON.parse(
    module.catalogueScene(DONOR, seed, palette, intensity, 'large', surface, simResolution),
  ) as SceneDocument;
  const base = Math.max(0, doc.palette.entries.findIndex((e) => e.role === 'base_wash'));
  const short = Math.min(width, height);
  const random = mulberry(seed || 1);
  const jitter = (spread: number) => (random() - 0.5) * spread;
  const events: { at_tick: number; op: unknown }[] = [];

  // Wet the whole sheet first, in overlapping rows, so the charges have water to travel through.
  const rows = 4;
  for (let i = 0; i < rows; i++) {
    const y = (i + 0.5) / rows;
    events.push({
      at_tick: 0,
      op: { water: { path: [[0.02, y], [0.98, y]], radius: [0.42, 0.42], water: 0.9, softness: 0.8 } },
    });
  }
  const clamp = (v: number) => Math.min(0.98, Math.max(0.02, v));
  const trend = Number.isFinite(slope) ? Math.min(1, Math.max(-1, slope)) : 0;
  const splotches = Array.from({ length: SPLOTCHES }, (_, i) => {
    const x = clamp((i + 0.5) / SPLOTCHES + jitter(0.03));
    const y = clamp(0.5 - trend * (x - 0.5) * 1.08 + jitter(0.02));
    return {
      at: [x, y],
      start: 1 + i * SPLOTCH_STAGGER_TICKS + Math.round(jitter(2)),
      size: 0.85 + random() * 0.3 + Math.abs(trend) * 0.2,
      phase: random() * 6.28,
    };
  });
  for (const { at, start, size, phase } of splotches) {
    for (let step = 0; step < GROW_TICKS; step++) {
      const t = (step + 1) / GROW_TICKS;
      const r = Math.min(1, (START_REACH + (END_REACH - START_REACH) * easeOut(t)) * size);
      // The centre drifts slowly as it grows, so a splotch swells lopsided rather than as a disc.
      const drift = r * 0.18 * (1 - Math.abs(trend) * 0.85);
      const centre = [
        clamp(at[0]! + (Math.sin(t * 3 + phase) * drift * short) / width),
        clamp(at[1]! + (Math.cos(t * 2.3 + phase) * drift * short) / height),
      ];
      events.push({
        at_tick: start + step,
        op: {
          brush: {
            path: [centre],
            radius: [r, r],
            pigment: 0,
            concentration: Math.min(1, CHARGE_CONCENTRATION * (0.4 + intensity * 0.857)),
            water: 0.5,
            softness: 0.85,
          },
        },
      });
    }
  }
  events.push({ at_tick: Math.round(TOTAL_TICKS * DRY_AT), op: { dry: { rate: DRY_RATE } } });
  events.push({ at_tick: TOTAL_TICKS, op: 'dry_all' });

  return JSON.stringify({
    ...doc,
    id: `bloom-${palette}-${seed}`,
    size_hint: [pxW, pxH],
    seed,
    sim_resolution: simResolution,
    palette: {
      name: doc.palette.name,
      entries: [colour ? { role: 'base_wash', pigment: bodyColour(colour) } : doc.palette.entries[base]!],
    },
    timeline: { total_ticks: TOTAL_TICKS, events },
  });
}
