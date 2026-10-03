import type { CropWindow, Surface } from '../types';
import { DEFAULT_INTENSITY } from '../types';
import type { WasmModule } from './wasm-types';
import { MAX_FRAME_ASPECT, dropSimResolution } from './drop-scene';
import { seededRandom } from './random';

export interface ReportWashOptions {
  seed?: number;
  surface?: Surface;
  dpr?: number;
}

interface SceneDocument {
  palette: { name: string; entries: { role: string; pigment: unknown }[] };
  [key: string]: unknown;
}

const DONOR = 'avatar-wash';
const PALETTE = 'moonlight';
/** Ticks the brush takes to cross the whole strip, whatever its width. */
const SWEEP_TICKS = 24;
/** The share of the strip's width the wash reaches before its edge breaks up. */
const WASH_REACH = [0.7, 0.85] as const;
const PASS_STAGGER_TICKS = 4;
/** How heavily each third of a pass is loaded, left to right: the brush runs out as it goes. */
const LOAD = [1, 0.9, 0.5] as const;
/** Ticks after the last pass when water is dropped back into the drying edge, so it blooms. */
const BACKRUN_AFTER_TICKS = 24;
const DRY_AFTER_TICKS = 70;
const SETTLE_TICKS = 110;
const DRY_RATE = 3;

/**
 * The window of the authored painting a `width` x `height` strip shows.
 *
 * A strip more elongated than the simulation grid can carry (see
 * {@link MAX_FRAME_ASPECT}) is painted on a canvas of that aspect at the
 * strip's width, and shows only the band through its middle.
 */
export function reportWashCrop(width: number, height: number): CropWindow {
  const band = Math.min(1, (height * MAX_FRAME_ASPECT) / width);
  return { x: 0, y: (1 - band) / 2, width: 1, height: band };
}

/**
 * One broad, loose wash laid wet into wet in overlapping horizontal passes
 * from the left, covering most of the strip's height and `WASH_REACH` of its
 * width. Its right edge fades unevenly and blooms where water runs back into
 * it, then dries to a hard line; drops of heavier pigment pool along the
 * bottom. The seed sets its reach, the edge and the passes. Authored in strip
 * pixels; show it through {@link reportWashCrop}.
 */
export function reportWashScene(
  module: Pick<WasmModule, 'catalogueScene'>,
  width: number,
  height: number,
  { seed = 0, surface = 'light', dpr = 1 }: ReportWashOptions = {},
): string {
  const crop = reportWashCrop(width, height);
  const canvasHeight = height / crop.height;
  const pxW = Math.max(1, Math.round(width * dpr));
  const pxH = Math.max(1, Math.round(canvasHeight * dpr));
  const simResolution = dropSimResolution(Math.max(pxW, pxH));
  const doc = JSON.parse(
    module.catalogueScene(DONOR, seed, PALETTE, DEFAULT_INTENSITY, 'large', surface, simResolution),
  ) as SceneDocument;
  const baseWash = Math.max(0, doc.palette.entries.findIndex((e) => e.role === 'base_wash'));
  const random = seededRandom(seed || 1);
  const between = (lo: number, hi: number) => lo + random() * (hi - lo);
  const short = Math.min(width, canvasHeight);
  const pt = (x: number, y: number): [number, number] => [
    Math.min(1, Math.max(0, x / width)),
    crop.y + Math.min(1, Math.max(0, y / height)) * crop.height,
  ];
  const rad = (r: number) => r / short;
  // The authoring `Style::conc` and `Style::water` factors at the default intensity.
  const conc = (share: number) => share * (0.4 + DEFAULT_INTENSITY * 0.857);
  const water = (share: number) => share * (0.8 + DEFAULT_INTENSITY * 0.3);

  const reach = between(...WASH_REACH) * width;
  const passCount = 5 + Math.floor(random() * 2);
  const longest = Math.floor(random() * passCount);
  const passes = Array.from({ length: passCount }, (_, i) => ({
    y: ((i + 0.5) / passCount + between(-0.04, 0.04)) * height,
    radius: between(0.18, 0.24) * height,
    x0: between(0, 0.03) * width,
    x1: i === longest ? reach : reach * between(0.72, 1),
    sag: between(-0.06, 0.06) * height,
  }));
  const events: { at_tick: number; op: unknown }[] = [];
  /** The stretch `t0..t1` of a pass from `x0` to `x1` that bows by `sag` at its middle. */
  const path = (x0: number, x1: number, y: number, sag: number, t0 = 0, t1 = 1) =>
    Array.from({ length: 5 }, (_, s) => {
      const t = t0 + ((t1 - t0) * s) / 4;
      return pt(x0 + (x1 - x0) * t, y + sag * Math.sin(Math.PI * t));
    });

  for (const { y, radius, x1 } of passes) {
    events.push({
      at_tick: 0,
      op: { water: { path: path(0, x1 * 1.05, y, 0), radius: [rad(radius * 1.5), rad(radius * 1.2)], water: water(1.2), softness: 0.85 } },
    });
  }
  let lastTick = 0;
  for (const [i, { y, radius, x0, x1, sag }] of passes.entries()) {
    let tick = 1 + i * PASS_STAGGER_TICKS;
    for (const [k, load] of LOAD.entries()) {
      const t0 = k / LOAD.length;
      const t1 = (k + 1) / LOAD.length;
      const steps = Math.max(1, Math.round((SWEEP_TICKS * (t1 - t0) * (x1 - x0)) / width));
      const stroke = path(x0, x1, y, sag, t0, t1);
      const taper = (at: number) => radius * (1 - 0.45 * (at / LOAD.length));
      for (let s = 0; s < steps; s++) {
        events.push({
          at_tick: tick + s,
          op: {
            brush: {
              path: stroke,
              radius: [rad(taper(k)), rad(taper(k + 1))],
              pigment: 0,
              concentration: conc(0.3 * load),
              water: water(1),
              softness: 0.75,
              ...(steps > 1 ? { span: [s / steps, (s + 1) / steps] } : {}),
            },
          },
        });
      }
      tick += steps;
    }
    lastTick = Math.max(lastTick, tick);
  }
  const pools = 2 + Math.floor(random() * 2);
  for (let i = 0; i < pools; i++) {
    events.push({
      at_tick: lastTick + 2 + i * 3,
      op: {
        dab: {
          center: pt(between(0.1, 0.8) * reach, between(0.7, 0.88) * height),
          radius: rad(between(0.1, 0.16) * height),
          pigment: 0,
          concentration: conc(0.6),
          water: water(0.5),
          softness: 0.8,
        },
      },
    });
  }
  for (const { y, x1 } of passes) {
    const at_tick = lastTick + BACKRUN_AFTER_TICKS + Math.floor(random() * 6);
    const centre = pt(x1 - between(0.01, 0.05) * width, y + between(-0.15, 0.15) * height);
    const radius = rad(between(0.15, 0.25) * height);
    events.push({ at_tick, op: { water: { path: [centre], radius: [radius, radius], water: water(0.7), softness: 0.6 } } });
  }
  const dryAt = lastTick + DRY_AFTER_TICKS;
  const totalTicks = dryAt + SETTLE_TICKS;
  events.push({ at_tick: dryAt, op: { dry: { rate: DRY_RATE } } });
  events.push({ at_tick: totalTicks, op: 'dry_all' });

  return JSON.stringify({
    ...doc,
    id: `report-wash-${seed}`,
    size_hint: [pxW, pxH],
    seed,
    sim_resolution: simResolution,
    // Every simulation cell carries every palette entry, so the scene keeps only the one it lays.
    palette: { name: doc.palette.name, entries: [doc.palette.entries[baseWash]!] },
    timeline: { total_ticks: totalTicks, events },
  });
}
