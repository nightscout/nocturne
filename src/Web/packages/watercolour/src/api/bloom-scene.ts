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
  dpr?: number;
  /** sRGB channels in 0..1; uses an opaque body pigment instead of the donor pigment. */
  colour?: readonly [number, number, number];
}

interface SceneDocument {
  palette: { name: string; entries: { role: string; pigment: unknown }[] };
  [key: string]: unknown;
}

const DONOR = 'avatar-wash';
const TOTAL_TICKS = 220;
const SPLOTCHES = 3;
const SPLOTCH_STAGGER_TICKS = 7;
const GROW_TICKS = 90;
const START_REACH = 0.07;
const END_REACH = 0.95;
const CHARGE_CONCENTRATION = 0.06;
const MAX_SIM_RESOLUTION = 320;
/** Compensates for gum-film and paper darkening; calibrated against the dried bloom. */
const SURFACE_GAIN = 1.5;
const BODY_SCATTER = 5;
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
