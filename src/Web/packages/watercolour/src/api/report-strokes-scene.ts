import type { CropWindow, Surface } from '../types';
import { DEFAULT_INTENSITY } from '../types';
import type { WasmModule } from './wasm-types';
import { MAX_FRAME_ASPECT, dropSimResolution } from './drop-scene';
import { seededRandom } from './random';

export interface ReportStrokesOptions {
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
/** Ticks the pen takes to cross the whole strip, whatever its width. */
const SWEEP_TICKS = 24;
const STROKE_STAGGER_TICKS = 6;
/** Ticks the strokes stay wet after the last one lands, to bleed where they cross. */
const BLEED_TICKS = 50;
const SETTLE_TICKS = 110;
const DRY_RATE = 3;
/** Centre-line samples per strip height. */
const SAMPLES_PER_HEIGHT = 0.5;

/** The strip height below which strokes stop thinning, so a rule-thin strip still gets brush-weight strokes. */
const MIN_STROKE_SCALE_PX = 28;
/**
 * The three weights, after the `header-motif` artwork: a thin stroke, a
 * broad one, and a darker one that crosses the broad one. Bellies are radii
 * as a share of the strip's height, floored at `MIN_STROKE_SCALE_PX`.
 */
const WEIGHTS = [
  { belly: [0.03, 0.045], shadow: false, concentration: 0.5, water: 0.5 },
  { belly: [0.09, 0.12], shadow: false, concentration: 0.55, water: 0.9 },
  { belly: [0.055, 0.07], shadow: true, concentration: 0.6, water: 0.8 },
] as const;
const THIN = 0;
const BROAD = 1;
const CROSSING = 2;
/** Where along a stroke the brush pressure changes; each stroke draws its own pressure at each. */
const PRESSURE_AT = [0, 0.08, 0.45, 0.8, 1];

/**
 * The window of the authored painting a `width` x `height` strip shows.
 *
 * A strip more elongated than the simulation grid can carry (see
 * {@link MAX_FRAME_ASPECT}) is painted on a canvas of that aspect at the
 * strip's width, and shows only the band through its middle.
 */
export function reportStrokesCrop(width: number, height: number): CropWindow {
  const band = Math.min(1, (height * MAX_FRAME_ASPECT) / width);
  return { x: 0, y: (1 - band) / 2, width: 1, height: band };
}

/**
 * Three loose brush strokes pulled left to right across the whole strip, of
 * different weights and slightly offset, laid wet one after another so a
 * later stroke bleeds into an earlier one where they cross. Each lands light,
 * presses to its belly and thins as the brush runs dry, ending in split
 * bristle streaks. The seed sets their heights, weights, curvature, overlap
 * and order. Show it through {@link reportStrokesCrop}.
 */
export function reportStrokesScene(
  module: Pick<WasmModule, 'catalogueScene'>,
  width: number,
  height: number,
  { seed = 0, surface = 'light', dpr = 1 }: ReportStrokesOptions = {},
): string {
  const crop = reportStrokesCrop(width, height);
  const canvasHeight = height / crop.height;
  const pxW = Math.max(1, Math.round(width * dpr));
  const pxH = Math.max(1, Math.round(canvasHeight * dpr));
  const simResolution = dropSimResolution(Math.max(pxW, pxH));
  const doc = JSON.parse(
    module.catalogueScene(DONOR, seed, PALETTE, DEFAULT_INTENSITY, 'large', surface, simResolution),
  ) as SceneDocument;
  const role = (name: string) => Math.max(0, doc.palette.entries.findIndex((e) => e.role === name));
  const base = role('base_wash');
  const shadow = role('shadow');
  const used = [...new Set([base, shadow])].sort((a, b) => a - b);

  const random = seededRandom(seed || 1);
  const between = (lo: number, hi: number) => lo + random() * (hi - lo);
  const shuffle = <T>(items: T[]) => {
    for (let i = items.length - 1; i > 0; i--) {
      const j = Math.floor(random() * (i + 1));
      [items[i], items[j]] = [items[j]!, items[i]!];
    }
    return items;
  };
  // The authoring `Style::conc` and `Style::water` factors at the default intensity.
  const conc = (share: number) => share * (0.4 + DEFAULT_INTENSITY * 0.857);
  const water = (share: number) => share * (0.8 + DEFAULT_INTENSITY * 0.3);
  const short = Math.min(width, canvasHeight);
  const pt = (x: number, y: number): [number, number] => [
    Math.min(1, Math.max(0, x / width)),
    crop.y + Math.min(1, Math.max(0, y / height)) * crop.height,
  ];
  const rad = (r: number) => r / short;

  const scale = Math.max(height, MIN_STROKE_SCALE_PX);
  const bellies = WEIGHTS.map(({ belly: [lo, hi] }) => between(lo, hi) * scale);
  const slots = shuffle([0.25, 0.5, 0.75].map((at) => (at + between(-0.06, 0.06)) * height));
  const within = (y: number, belly: number) => Math.min(height - belly * 1.2, Math.max(belly * 1.2, y));
  const strokes = WEIGHTS.map((weight, i) => {
    const belly = bellies[i]!;
    const start = slots[i]!;
    const end =
      i === CROSSING
        ? slots[BROAD]! - (start - slots[BROAD]!) * between(0.4, 0.9)
        : start + between(-0.18, 0.18) * height;
    return {
      weight,
      belly,
      x0: between(0, 0.05) * width,
      x1: between(0.88, 0.95) * width,
      start: within(start, belly),
      end: within(end, belly),
      bow: between(-0.12, 0.12) * height,
      wobble: { size: 0.03 * height, cycles: between(1.5, 3), phase: random() * Math.PI * 2 },
      pressure: [0.3, 1, between(0.75, 1), between(0.55, 0.8), 0.35],
    };
  });
  const order = shuffle([THIN, BROAD, CROSSING]);

  const events: { at_tick: number; op: unknown }[] = [];
  let lastTick = 0;
  order.forEach((which, laid) => {
    const { weight, belly, x0, x1, start, end, bow, wobble, pressure } = strokes[which]!;
    const length = x1 - x0;
    const centre = (t: number) =>
      start + (end - start) * t + bow * Math.sin(Math.PI * t) + wobble.size * Math.sin(2 * Math.PI * wobble.cycles * t + wobble.phase);
    const along = (t: number, offset = 0) => pt(x0 + length * t, within(centre(t), belly) + offset);
    const pigment = used.indexOf(weight.shadow ? shadow : base);
    let tick = laid * STROKE_STAGGER_TICKS;
    for (let k = 0; k + 1 < PRESSURE_AT.length; k++) {
      const t0 = PRESSURE_AT[k]!;
      const t1 = PRESSURE_AT[k + 1]!;
      const samples = Math.max(2, Math.ceil(((t1 - t0) * length * SAMPLES_PER_HEIGHT) / height) + 1);
      const path = Array.from({ length: samples }, (_, s) => along(t0 + ((t1 - t0) * s) / (samples - 1)));
      const steps = Math.max(1, Math.round(SWEEP_TICKS * (t1 - t0) * (length / width)));
      for (let s = 0; s < steps; s++) {
        events.push({
          at_tick: tick + s,
          op: {
            brush: {
              path,
              radius: [rad(belly * pressure[k]!), rad(belly * pressure[k + 1]!)],
              pigment,
              concentration: conc(weight.concentration),
              water: water(weight.water),
              softness: 0.5,
              ...(steps > 1 ? { span: [s / steps, (s + 1) / steps] } : {}),
            },
          },
        });
      }
      tick += steps;
    }
    for (const side of [-1, 0, 1]) {
      const t0 = 1 - between(0.04, 0.1);
      const t1 = t0 + between(0.08, 0.14);
      events.push({
        at_tick: tick,
        op: {
          brush: {
            path: [along(t0, side * belly * 0.35), along(t1, side * belly * 0.35 + side * 0.01 * height)],
            radius: [rad(belly * 0.18), rad(belly * 0.06)],
            pigment,
            concentration: conc(weight.concentration * 1.2),
            water: water(0.08),
            softness: 0.15,
          },
        },
      });
    }
    lastTick = Math.max(lastTick, tick);
  });
  const dryAt = lastTick + BLEED_TICKS;
  const totalTicks = dryAt + SETTLE_TICKS;
  events.push({ at_tick: dryAt, op: { dry: { rate: DRY_RATE } } });
  events.push({ at_tick: totalTicks, op: 'dry_all' });

  return JSON.stringify({
    ...doc,
    id: `report-strokes-${seed}`,
    size_hint: [pxW, pxH],
    seed,
    sim_resolution: simResolution,
    palette: { name: doc.palette.name, entries: used.map((at) => doc.palette.entries[at]!) },
    timeline: { total_ticks: totalTicks, events },
  });
}
