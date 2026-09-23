import type { PaletteId, Surface } from '../types';
import { DEFAULT_INTENSITY } from '../types';
import type { WasmModule } from './wasm-types';
import { type Box, type DropMark, MAX_RADIUS, bleedFor } from './drop-stroke';

/**
 * How the stroke is laid down.
 *
 * `stamp` deposits the pigment stroke alone. `wet` charges the paper with
 * water along the path first and drops the pigment into it. The wash then
 * blooms with a soft edge and granulates rather than drying as a flat field.
 */
export type DropDeposit = 'stamp' | 'wet';

export interface DropSceneOptions {
  palette?: PaletteId;
  surface?: Surface;
  seed?: number;
  intensity?: number;
  deposit?: DropDeposit;
  /** Device pixel ratio the canvas will be backed at; sizes the simulation grid. */
  dpr?: number;
}

export interface DropScene {
  sceneJson: string;
  /** Where the canvas sits, in surface pixels. */
  frame: Box;
}

/** The parts of a scene document the assembler reads or writes. */
interface SceneDocument {
  version: number;
  id: string;
  size_hint: [number, number];
  seed: number;
  sim_resolution: number;
  background: string;
  paper: unknown;
  palette: { name: string; entries: { role: string; pigment: unknown }[] };
  timeline: { total_ticks: number; events: { at_tick: number; op: Record<string, unknown> }[] };
}

/**
 * The catalogue artwork whose paper and palette the drop borrows. Only its
 * timeline is replaced. The pigments and the sheet stay the engine's own. A
 * drop therefore matches every other mark in the palette with no second
 * source of pigment data.
 */
const DONOR = 'avatar-wash';
/** Ticks the wash gets to spread and dry; the whole clock is settle. */
const TOTAL_TICKS = 160;
/** When the sheet starts drying, as a fraction of the ticks. */
const DRY_AT = 0.5;
const DRY_RATE = 3.5;
/**
 * Room around the paint for the bloom to spread into before the canvas edge
 * clips it, as a multiple of the fat-end radius.
 */
const BLOOM_PAD = 1.2;
/**
 * The simulation grid is square and stretched to the canvas. A very elongated
 * canvas therefore has cells several times wider than they are tall. Water
 * then spreads along the stroke rather than across it, and a thin edge shows
 * as blocks. The frame is padded on its short side to keep that in check.
 */
const MAX_FRAME_ASPECT = 3;
/**
 * Grid cells per device pixel of the canvas's long edge, and the largest grid
 * a drop runs on, on either ground. A light hero is capped at 256; a drop's
 * edge is the brush stamp itself, rasterised on the grid. At 256 over a
 * card-wide canvas a hairline border is two cells thick and pixelated. 512
 * costs about four times a 256 tick. A drop can
 * afford it: it runs alone for well under a second, with one checkpoint and
 * two pigments.
 */
const CELLS_PER_PIXEL = 1;
const MAX_DROP_SIM_RESOLUTION = 512;

export function dropSimResolution(longEdgePx: number): number {
  const cells = Math.ceil((longEdgePx * CELLS_PER_PIXEL) / 32) * 32;
  return Math.min(MAX_DROP_SIM_RESOLUTION, Math.max(64, cells));
}
/** How much lighter a wash round a label is laid than a mark beside one. */
const WASH_CONCENTRATION = 0.55;
/**
 * How much lighter a glaze is laid than a mark beside a label. It has no
 * reserves, so the label sits on the paint and has to read through it.
 */
const GLAZE_CONCENTRATION = 0.45;
/**
 * When a reserve is lifted, and how much of the paint under it goes.
 *
 * Lifted the tick after the pigment lands, the cells are emptied while the
 * wash is still wet. An emptied cell dries, and a dry cell is a boundary the
 * flow does not cross, so the paint pools against the lifted edge instead of
 * creeping back over the copy.
 */
const RESERVE_TICK = 2;
const RESERVE_STRENGTH = 1;
const RESERVE_SOFTNESS = 0.5;
/**
 * Corner radius of a reserve, in pixels. The engine lifts along capsules, so
 * a reserve is a stack of them, and this is each one's half-height: one
 * capsule the height of a block would round its corners away.
 */
const RESERVE_CORNER = 10;
/** A splotch is a large field of paint, so it is laid lighter than a stroke. */
const SPLOTCH_CONCENTRATION = 0.7;
/**
 * Ticks between one wet drop landing and the next, largest first.
 *
 * Drops that land on the same tick read as one stamp. The reveal gives the
 * brushwork a fixed share of the wall clock, up to the last stroke event, so
 * the stagger stretches that share rather than adding to it. At the default
 * reveal three ticks is about 45 ms between two drops, 40 ms across three.
 */
const DAB_STAGGER_TICKS = 3;

/**
 * The gestures the pen draws along their path rather than lays down whole.
 *
 * A long stroke that appears whole at the first tick reads as a stamp
 * dropped on the row, not a brush pulled across it. A drop, a splotch and a
 * fill are laid down whole, as a brush loaded and pressed once would; a wash
 * drawn along its path would paint over its reserves before they were lifted.
 */
const DRAWN: ReadonlySet<DropMark['kind']> = new Set(['stroke', 'border']);
/** Path length, in pixels, each laydown step covers, and the most steps a stroke is cut into. */
const SWEEP_STEP_PX = 40;
const MAX_SWEEP_STEPS = 12;
/**
 * How fast the pen travels, in pixels per millisecond: a quick, confident
 * pull, so a row-wide stroke takes about 300 ms.
 */
const PEN_SPEED = 3;

const pathLength = (path: readonly { x: number; y: number }[]) =>
  path.slice(1).reduce((sum, p, i) => sum + Math.hypot(p.x - path[i]!.x, p.y - path[i]!.y), 0);

/** Laydown steps a stroke of the mark is cut into; 1 for a mark laid down whole. */
export function sweepSteps(mark: DropMark, path: readonly { x: number; y: number }[]): number {
  if (!DRAWN.has(mark.kind)) return 1;
  return Math.min(MAX_SWEEP_STEPS, Math.max(1, Math.ceil(pathLength(path) / SWEEP_STEP_PX)));
}

/**
 * How long the pen spends drawing the mark, in milliseconds; 0 for a mark
 * laid down whole. The host gives this share of the reveal to the brushwork.
 */
export function brushworkMs(mark: DropMark): number {
  if (!DRAWN.has(mark.kind)) return 0;
  return mark.strokes.reduce((sum, st) => sum + pathLength(st.path), 0) / PEN_SPEED;
}

/** Concentration and water for the surface, mirroring the authoring `Style` factors. */
function medium(surface: Surface, intensity: number) {
  const conc = (base: number) => Math.min(1, base * (0.4 + intensity * 0.857));
  const water = (base: number) => Math.min(1.5, base * (0.8 + intensity * 0.3));
  // A luminous wash on a dark ground saturates, so it is laid lighter.
  return surface === 'dark'
    ? { conc: conc(0.2), water: water(0.55), spatterConc: conc(0.4) }
    : { conc: conc(0.48), water: water(0.5), spatterConc: conc(0.8) };
}

function roleIndex(doc: SceneDocument, role: string): number {
  const at = doc.palette.entries.findIndex((e) => e.role === role);
  return at < 0 ? 0 : at;
}

/**
 * The canvas box for a stroke: its paint plus bloom room, clipped to the
 * bleed the surface allows. It is never more elongated than the simulation
 * grid can carry.
 *
 * The clip never cuts off a point the scene is built from. The engine rejects
 * a scene whose path or dab centre lies off the canvas, and a rejected scene
 * draws nothing. A splotch's centre sits well past the edge when the clear
 * band is narrower than the circle. The canvas reaches out to it, and the
 * surface's own overflow clips the paint instead.
 */
export function strokeFrame(mark: DropMark, cw: number, ch: number): Box {
  const margin = bleedFor(cw, ch);
  const widest = Math.max(0, ...mark.strokes.map((st) => st.radius[0]), ...mark.dabs.map((d) => d.r));
  const pad = Math.max(12, widest * BLOOM_PAD);
  const b = mark.bounds;
  const points = [...mark.strokes.flatMap((st) => st.path), ...mark.dabs];
  const minX = Math.min(-margin, ...points.map((p) => p.x));
  const minY = Math.min(-margin, ...points.map((p) => p.y));
  const maxX = Math.max(cw + margin, ...points.map((p) => p.x));
  const maxY = Math.max(ch + margin, ...points.map((p) => p.y));
  let x0 = Math.max(minX, b.x - pad);
  let y0 = Math.max(minY, b.y - pad);
  let x1 = Math.min(maxX, b.x + b.w + pad);
  let y1 = Math.min(maxY, b.y + b.h + pad);
  /** Widens `[lo, hi]` to `need`, centred, sliding to stay within `[min, max]`. */
  const widen = (lo: number, hi: number, need: number, min: number, max: number): [number, number] => {
    const extra = need - (hi - lo);
    if (extra <= 0) return [lo, hi];
    lo -= extra / 2;
    hi += extra / 2;
    if (lo < min) {
      hi = Math.min(max, hi + (min - lo));
      lo = min;
    } else if (hi > max) {
      lo = Math.max(min, lo - (hi - max));
      hi = max;
    }
    return [lo, hi];
  };
  const w = x1 - x0;
  const h = y1 - y0;
  if (w > h * MAX_FRAME_ASPECT) [y0, y1] = widen(y0, y1, w / MAX_FRAME_ASPECT, minY, maxY);
  else if (h > w * MAX_FRAME_ASPECT) [x0, x1] = widen(x0, x1, h / MAX_FRAME_ASPECT, minX, maxX);
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

/**
 * A scene that lays the mark down in its first few ticks and spends the rest
 * letting the ink spread into the paper. A stroke lands whole at tick zero;
 * drops land one after another, `DAB_STAGGER_TICKS` apart. Nothing in it
 * draws; what the viewer watches is the wash settling.
 */
export function dropScene(
  module: Pick<WasmModule, 'catalogueScene'>,
  mark: DropMark,
  cw: number,
  ch: number,
  { palette = 'water', surface = 'light', seed = 0, intensity = DEFAULT_INTENSITY, deposit = 'wet', dpr = 1 }: DropSceneOptions = {},
): DropScene {
  const frame = strokeFrame(mark, cw, ch);
  const width = Math.max(1, Math.round(frame.w * dpr));
  const height = Math.max(1, Math.round(frame.h * dpr));
  const simResolution = dropSimResolution(Math.max(width, height));
  const doc = JSON.parse(
    module.catalogueScene(DONOR, seed, palette, intensity, 'large', surface, simResolution),
  ) as SceneDocument;

  // Scene coordinates run 0..1 over each canvas axis; radii are in units of
  // the canvas's short side, the engine's isotropic metric.
  const short = Math.min(frame.w, frame.h);
  const pt = (p: { x: number; y: number }): [number, number] => [(p.x - frame.x) / frame.w, (p.y - frame.y) / frame.h];
  const rad = (r: number) => r / short;

  const base = roleIndex(doc, 'base_wash');
  const shadow = roleIndex(doc, 'shadow');
  const wet = deposit === 'wet';
  // Every simulation cell carries state for every palette entry, so the scene
  // keeps only the pigments its events lay down. The trimmed entries and the
  // events' indices both read from this list, which keeps the two in step.
  const used = [...new Set(wet ? [base, shadow] : [base])].sort((a, b) => a - b);
  const pigmentIndex = (donorAt: number) => used.indexOf(donorAt);
  const m = medium(surface, intensity);
  // A wash sits behind a label, so it is laid lighter than a mark beside one.
  const share: Partial<Record<DropMark['kind'], number>> = {
    wash: WASH_CONCENTRATION,
    glaze: GLAZE_CONCENTRATION,
    splotch: SPLOTCH_CONCENTRATION,
  };
  const conc = m.conc * (share[mark.kind] ?? 1);
  const water = m.water;
  const events: SceneDocument['timeline']['events'] = [];

  /**
   * A stamp's softness for a brush of radius `r`.
   *
   * Softness is a share of the radius, so a splotch's 300 px circle laid at a
   * stroke's softness fades over 200 px and dries to a mist with no edge. Past
   * the largest stroke brush the fade is held at that brush's width instead.
   */
  const soft = (base: number, r: number) => base * Math.min(1, MAX_RADIUS / Math.max(r, 1e-6));
  /** `span` for step `i` of `steps`, or none for a stroke laid down whole. */
  const spanOf = (i: number, steps: number) => (steps > 1 ? { span: [i / steps, (i + 1) / steps] } : {});
  /**
   * A stroke or a wet dab: water first, pigment into it, a darker drop at the
   * head. A drawn stroke is laid in `steps` spans along its path, one a tick,
   * each keeping the whole stroke's radius profile, so the union is the stroke
   * laid whole and only the timing differs.
   */
  const lay = (path: [number, number][], r0: number, r1: number, start = 0, steps = 1) => {
    if (wet) {
      for (let i = 0; i < steps; i++) {
        events.push({
          at_tick: start + i,
          op: { water: { path, radius: [rad(r0 * 1.25), rad(r1 * 1.25)], water: water * 1.6, softness: soft(0.8, r0 * 1.25), ...spanOf(i, steps) } },
        });
        events.push({
          at_tick: start + i + 1,
          op: { brush: { path, radius: [rad(r0), rad(r1)], pigment: pigmentIndex(base), concentration: conc, water: water * 0.6, softness: soft(0.6, r0), ...spanOf(i, steps) } },
        });
      }
      events.push({
        at_tick: start + 3,
        op: {
          brush: {
            path: [path[0]!],
            radius: [rad(r0 * 0.45), rad(r0 * 0.45)],
            pigment: pigmentIndex(shadow),
            concentration: conc * 1.1,
            water: water * 0.5,
            softness: soft(0.8, r0 * 0.45),
          },
        },
      });
    } else {
      for (let i = 0; i < steps; i++) {
        events.push({
          at_tick: start + i,
          op: { brush: { path, radius: [rad(r0), rad(r1)], pigment: pigmentIndex(base), concentration: conc, water, softness: soft(0.6, r0), ...spanOf(i, steps) } },
        });
      }
    }
  };

  for (const st of mark.strokes) lay(st.path.map(pt), st.radius[0], st.radius[1], 0, sweepSteps(mark, st.path));
  // Each reserve as horizontal capsules stacked down it, one per row. Rows
  // sit closer than their diameter, so the stack has no gaps between them.
  for (const b of mark.reserves ?? []) {
    const r = Math.min(RESERVE_CORNER, b.w / 2, b.h / 2);
    const rows = Math.max(1, Math.ceil((b.h - 2 * r) / r) + 1);
    const x0 = b.x + r;
    const x1 = b.x + b.w - r;
    for (let i = 0; i < rows; i++) {
      const y = rows === 1 ? b.y + b.h / 2 : b.y + r + ((b.h - 2 * r) * i) / (rows - 1);
      events.push({
        at_tick: RESERVE_TICK,
        op: {
          lift: {
            path: [pt({ x: x0, y }), pt({ x: x1, y })],
            radius: [rad(r), rad(r)],
            strength: RESERVE_STRENGTH,
            softness: RESERVE_SOFTNESS,
          },
        },
      });
    }
  }
  let landed = 0;
  for (const d of mark.dabs) {
    if (d.wet) {
      lay([pt(d)], d.r, d.r, landed++ * DAB_STAGGER_TICKS);
      continue;
    }
    // Droplets are flicked, not washed: little water, so they dry with an edge.
    events.push({
      at_tick: 0,
      op: {
        brush: {
          path: [pt(d)],
          radius: [rad(d.r), rad(d.r)],
          pigment: pigmentIndex(base),
          concentration: m.spatterConc,
          water: water * 0.3,
          softness: 0.3,
        },
      },
    });
  }
  events.push({ at_tick: Math.round(TOTAL_TICKS * DRY_AT), op: { dry: { rate: DRY_RATE } } });
  events.push({ at_tick: TOTAL_TICKS, op: 'dry_all' as unknown as Record<string, unknown> });

  const scene: SceneDocument = {
    ...doc,
    id: `drop-${mark.kind}-${palette}-${seed}`,
    size_hint: [width, height],
    seed,
    sim_resolution: simResolution,
    palette: { name: doc.palette.name, entries: used.map((at) => doc.palette.entries[at]!) },
    timeline: { total_ticks: TOTAL_TICKS, events },
  };
  return { sceneJson: JSON.stringify(scene), frame };
}
