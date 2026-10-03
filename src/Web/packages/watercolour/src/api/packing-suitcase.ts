import type { LiveClock, SceneOp, TimedOp } from '../types';
import { DEFAULT_INTENSITY } from '../types';
import { dropSimResolution } from './drop-scene';
import type { WasmModule } from './wasm-types';

/**
 * The packing-list suitcase, painted one band per packed item.
 *
 * The body fills from the top in horizontal bands, in the order items are
 * packed. A band takes the unpainted height divided by the items still to
 * pack, counting itself, so the body is full exactly when the last item goes
 * in. Each band overlaps the one above, which is where its timing shows:
 * packed while the band above is wet it mixes into it, packed after that band
 * dried it glazes over it with a crisp seam. Unpacking lifts the lowest band.
 * A full body gets indigo hardware; a pack beyond full lays a thin glaze.
 */

export interface SuitcaseGeometry {
  /** The body silhouette in normalised canvas units. */
  outline: readonly (readonly [number, number])[];
  /** Palette indices. */
  pigments: { bands: readonly number[]; hardware: number; clasp: number };
}

const LIVE_TICKS_PER_SECOND = 30;
/**
 * Share of each wet cell's film a live tick evaporates. At 30 ticks a second
 * a band can still bleed at about 4 s and is close to dry by 8 s.
 */
const LIVE_SETTLE_SHARE = 0.009;

/** The session the painter's operations are timed for: dry, and stopped, 8 s after the last. */
export const PACKING_LIVE_CLOCK: LiveClock = { ticksPerSecond: LIVE_TICKS_PER_SECOND, idleTicks: 8 * LIVE_TICKS_PER_SECOND };

const OVERLAP = 0.25;
/** Spacing of hatch rows inside a band; a thinner band is a single row. */
const ROW_PITCH = 0.09;
/** Row radius as a share of the row pitch; rows overlap like the catalogue hatch. */
const ROW_RADIUS = 0.8;
/** How far the hatch runs past the body; the mask trims it to the silhouette. */
const HATCH_OVERRUN = 0.12;
const BAND_TICKS = 10;
const BAND_CONCENTRATION = 0.44;
const BAND_WATER = 0.5;
const BAND_SOFTNESS = 0.75;
const PREWET_TICKS = 3;
const PREWET_WATER = 0.6;
/**
 * The pre-wet covers the band's own slot, a little over. Wetting the overlap
 * too floods a tall band above it.
 */
const PREWET_REACH = 1.15;
/** A lift leaves a ghost of the band; a stronger one reads as a hole in the body. */
const LIFT_STRENGTH = 0.6;
/**
 * The body under the hardware is lifted nearly to paper: indigo over a warm
 * band dries olive, and gold over indigo grey.
 */
const CLEARING_STRENGTH = 0.9;
const LIFT_STAGGER_TICKS = 4;
const GLAZE_CONCENTRATION = 0.12;
const GLAZE_WATER = 0.3;
const MASK_FEATHER = 0.012;
const HARDWARE_WATER = 0.3;
/**
 * The hardware waits for the last band to lose most of its water, and the
 * clasp for the strap: indigo laid into a wet warm band mixes to olive, gold
 * into wet indigo to green.
 */
const HARDWARE_WAIT_TICKS = 4 * LIVE_TICKS_PER_SECOND;
/** Ticks a replayed band gets to spread before the sheet is dried for the next, or for the hardware. */
const REPLAY_SPREAD_TICKS = 6;
const CLASP_WAIT_TICKS = 2 * LIVE_TICKS_PER_SECOND;
const STRAP_Y = 0.59;
const STRAP_RADIUS = 0.05;
const STRAP_OVERHANG = 0.02;
const HANDLE: readonly [number, number][] = [
  [0.39, 0.34],
  [0.39, 0.22],
  [0.61, 0.22],
  [0.61, 0.34],
];
const HANDLE_RADIUS = 0.028;
const CLASP_RADIUS = 0.038;

interface Band {
  top: number;
  bottom: number;
  pigment: number;
}

interface Stroke {
  path: [number, number][];
  radius: number;
}

/** A boustrophedon from `top` to `bottom`, `rows` passes across `x0..x1`. */
function hatch(x0: number, x1: number, top: number, bottom: number, rows: number): [number, number][] {
  const pitch = (bottom - top) / rows;
  const path: [number, number][] = [];
  for (let i = 0; i < rows; i++) {
    const y = top + pitch * (i + 0.5);
    const [a, b] = i % 2 === 0 ? [x0, x1] : [x1, x0];
    path.push([a, y], [b, y]);
  }
  return path;
}

/** Splits one stroke over `ticks` ticks, as the catalogue lays its strokes. */
function spread(kind: 'brush' | 'water', body: Record<string, unknown>, ticks: number, from: number): TimedOp[] {
  return Array.from({ length: ticks }, (_, i) => ({
    afterTicks: from + i,
    op: { [kind]: { ...body, span: [i / ticks, (i + 1) / ticks] } },
  }));
}

/** FNV-1a, so a category keeps its pigment across visits and devices. */
function categoryHash(category: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < category.length; i++) {
    h ^= category.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

export class PackingSuitcase {
  private readonly x0: number;
  private readonly x1: number;
  private readonly y0: number;
  private readonly y1: number;
  private readonly bands: Band[] = [];
  private hardware = false;

  constructor(private readonly geometry: SuitcaseGeometry) {
    const xs = geometry.outline.map((p) => p[0]);
    const ys = geometry.outline.map((p) => p[1]);
    [this.x0, this.x1, this.y0, this.y1] = [Math.min(...xs), Math.max(...xs), Math.min(...ys), Math.max(...ys)];
  }

  /**
   * One item packed, with `stillUnpacked` items left after it. Returns its
   * band, or a glaze when the body is already full, and the hardware when
   * this pack completes the list. `replay` lays the hardware for a sheet the
   * caller dries itself, with a `dry_all` in place of the live waits.
   */
  pack(category: string, stillUnpacked: number, replay = false): TimedOp[] {
    const filled = this.bands.at(-1)?.bottom ?? this.y0;
    const pigment = this.bandPigment(category);
    let paint: TimedOp[];
    if (this.y1 - filled < 1e-4) {
      paint = this.glaze(pigment);
    } else {
      const band = { top: filled, bottom: filled + (this.y1 - filled) / (stillUnpacked + 1), pigment };
      this.bands.push(band);
      paint = this.band(band);
    }
    // The handle is painted with the mask cleared, so every pack restores it.
    const ops = [{ afterTicks: 0, op: this.mask() }, ...paint];
    if (stillUnpacked === 0 && !this.hardware) {
      this.hardware = true;
      ops.push(...this.hardwareOps(replay));
    }
    return ops;
  }

  /**
   * The list changed with no pack: an unpacked item was removed, leaving
   * `stillUnpacked`. When that completes the list, the lowest band runs on to
   * the bottom of the body and the hardware goes on.
   */
  countChanged(stillUnpacked: number): TimedOp[] {
    const last = this.bands.at(-1);
    if (stillUnpacked > 0 || this.hardware || !last) return [];
    const ops = [{ afterTicks: 0, op: this.mask() }];
    if (this.y1 - last.bottom >= 1e-4) {
      ops.push(...this.band({ top: last.bottom, bottom: this.y1, pigment: last.pigment }));
      last.bottom = this.y1;
    }
    this.hardware = true;
    ops.push(...this.hardwareOps(false));
    return ops;
  }

  /** One item unpacked: a complete suitcase loses its hardware, then the lowest band lifts. */
  unpack(): TimedOp[] {
    const ops: TimedOp[] = [];
    if (this.hardware) {
      this.hardware = false;
      const [strap, handle] = this.hardwarePaths();
      // The strap's lift covers the clasp; lifting the clasp again takes it to bare paper.
      ops.push(...this.lift([strap, handle], 0, CLEARING_STRENGTH));
    }
    const band = this.bands.pop();
    if (band) ops.push(...this.lift([this.bandStroke(band)], ops.length * LIFT_STAGGER_TICKS, LIFT_STRENGTH));
    return ops;
  }

  private bandPigment(category: string): number {
    const { bands } = this.geometry.pigments;
    return bands[categoryHash(category) % bands.length]!;
  }

  private mask(): SceneOp {
    return { set_mask: { mask: { polygon: { points: this.geometry.outline, feather: MASK_FEATHER } } } };
  }

  private hatchAcross(top: number, bottom: number): Stroke {
    const rows = Math.max(1, Math.round((bottom - top) / ROW_PITCH));
    const x0 = Math.max(0, this.x0 - HATCH_OVERRUN);
    const x1 = Math.min(1, this.x1 + HATCH_OVERRUN);
    return { path: hatch(x0, x1, top, bottom, rows), radius: ((bottom - top) / rows) * ROW_RADIUS };
  }

  private bandStroke(band: Band): Stroke {
    const overlap = band.top > this.y0 ? (band.bottom - band.top) * OVERLAP : 0;
    return this.hatchAcross(band.top - overlap, band.bottom);
  }

  private band(band: Band): TimedOp[] {
    const { path, radius } = this.bandStroke(band);
    const centre = (band.top + band.bottom) / 2;
    const wetRadius = ((band.bottom - band.top) / 2) * PREWET_REACH;
    return [
      ...spread(
        'water',
        {
          path: [
            [Math.max(0, this.x0 - HATCH_OVERRUN), centre],
            [Math.min(1, this.x1 + HATCH_OVERRUN), centre],
          ],
          radius: [wetRadius, wetRadius],
          water: PREWET_WATER,
          softness: 0.3,
        },
        PREWET_TICKS,
        0,
      ),
      ...spread(
        'brush',
        { path, radius: [radius, radius], pigment: band.pigment, concentration: BAND_CONCENTRATION, water: BAND_WATER, softness: BAND_SOFTNESS },
        BAND_TICKS - PREWET_TICKS,
        PREWET_TICKS,
      ),
    ];
  }

  private glaze(pigment: number): TimedOp[] {
    const { path, radius } = this.hatchAcross(this.y0, this.y1);
    return spread(
      'brush',
      { path, radius: [radius, radius], pigment, concentration: GLAZE_CONCENTRATION, water: GLAZE_WATER, softness: BAND_SOFTNESS },
      BAND_TICKS,
      0,
    );
  }

  private hardwarePaths(): [Stroke, Stroke, Stroke] {
    const centre = (this.x0 + this.x1) / 2;
    return [
      {
        path: [
          [this.x0 - STRAP_OVERHANG, STRAP_Y],
          [this.x1 + STRAP_OVERHANG, STRAP_Y],
        ],
        radius: STRAP_RADIUS,
      },
      { path: HANDLE.map(([x, y]) => [x, y]), radius: HANDLE_RADIUS },
      { path: [[centre, STRAP_Y]], radius: CLASP_RADIUS },
    ];
  }

  private hardwareOps(replay: boolean): TimedOp[] {
    const [strap, handle, clasp] = this.hardwarePaths();
    const { hardware, clasp: claspPigment } = this.geometry.pigments;
    const brush = (s: Stroke, pigment: number, concentration: number, ticks: number, at: number) =>
      spread(
        'brush',
        { path: s.path, radius: [s.radius, s.radius], pigment, concentration, water: HARDWARE_WATER, softness: 0.5 },
        ticks,
        at,
      );
    const start = BAND_TICKS + (replay ? REPLAY_SPREAD_TICKS : HARDWARE_WAIT_TICKS);
    const strapAt = start + LIFT_STAGGER_TICKS;
    const handleAt = strapAt + 12;
    const claspAt = handleAt + 10 + (replay ? 2 : CLASP_WAIT_TICKS);
    return [
      ...(replay ? [{ afterTicks: start - 1, op: 'dry_all' }] : []),
      ...this.lift([strap], start, CLEARING_STRENGTH),
      ...brush(strap, hardware, 0.5, 12, strapAt),
      { afterTicks: handleAt, op: 'clear_mask' },
      ...brush(handle, hardware, 0.85, 10, handleAt),
      ...(replay ? [{ afterTicks: claspAt - 2, op: 'dry_all' }] : []),
      ...this.lift([clasp], claspAt - 1, CLEARING_STRENGTH),
      ...brush(clasp, claspPigment, 0.9, 2, claspAt),
    ];
  }

  /** A lift is one operation: split into spans, each span's end cap lifts again where the next begins. */
  private lift(strokes: Stroke[], from: number, strength: number): TimedOp[] {
    return strokes.map((s, i) => ({
      afterTicks: from + i * LIFT_STAGGER_TICKS,
      op: { lift: { path: s.path, radius: [s.radius * 1.1, s.radius * 1.1], strength, softness: 0.6, span: [0, 1] } },
    }));
  }
}

interface SceneDocument {
  palette: { entries: { role: string }[] };
  timeline: { events: { op: SceneOp }[] };
  [key: string]: unknown;
}

/** The catalogue artwork that donates the paper, the palette and the silhouette. */
const DONOR = 'suitcase';
const DONOR_PALETTE = 'dusk';
const REPLAY_SETTLE_SHARE = 0.25;
/** Ticks after the last replayed operation for the reveal to come to rest. */
const REPLAY_TAIL_TICKS = 20;
/** The grid the prototype was tuned on; a larger one spreads the same strokes over more cells. */
const MAX_SIM_RESOLUTION = 320;

export interface PackingSuitcaseOptions {
  /** Categories of the items already packed, in the order they are painted. */
  packed: readonly string[];
  /** Items still to pack. */
  unpacked: number;
  seed?: number;
  dpr?: number;
}

export interface PackingSuitcaseScene {
  sceneJson: string;
  /** The painter, holding the replayed bands, for the live session to continue. */
  painter: PackingSuitcase;
}

function donorOutline(doc: SceneDocument): [number, number][] {
  for (const { op } of doc.timeline.events) {
    const points = typeof op === 'object' ? (op.set_mask as { mask?: { polygon?: { points: [number, number][] } } })?.mask?.polygon?.points : undefined;
    if (points) return points;
  }
  throw new Error(`catalogue ${DONOR} has no polygon mask to take the silhouette from`);
}

/**
 * The suitcase as a reload finds it, `width` x `height` CSS pixels: the
 * `packed` categories painted in order, each band dried before the next,
 * with `unpacked` items still to go.
 */
export function packingSuitcaseScene(
  module: Pick<WasmModule, 'catalogueScene'>,
  width: number,
  height: number,
  { packed, unpacked, seed = 0, dpr = 1 }: PackingSuitcaseOptions,
): PackingSuitcaseScene {
  const simResolution = Math.min(MAX_SIM_RESOLUTION, dropSimResolution(Math.round(Math.max(width, height) * dpr)));
  const doc = JSON.parse(
    module.catalogueScene(DONOR, seed, DONOR_PALETTE, DEFAULT_INTENSITY, 'large', 'light', simResolution),
  ) as SceneDocument;
  const role = (name: string) => {
    const index = doc.palette.entries.findIndex((e) => e.role === name);
    if (index < 0) throw new Error(`catalogue ${DONOR} in ${DONOR_PALETTE} has no ${name} pigment`);
    return index;
  };
  const painter = new PackingSuitcase({
    outline: donorOutline(doc),
    pigments: { bands: [role('base_wash'), role('accent'), role('glow')], hardware: role('shadow'), clasp: role('glow') },
  });

  const events: { at_tick: number; op: SceneOp }[] = [];
  let tick = 0;
  packed.forEach((category, i) => {
    const ops = painter.pack(category, packed.length - 1 - i + unpacked, true);
    const end = Math.max(...ops.map((o) => o.afterTicks));
    events.push(
      ...ops.map(({ afterTicks, op }) => ({ at_tick: tick + afterTicks, op })),
      { at_tick: tick + BAND_TICKS, op: { settle: { share: REPLAY_SETTLE_SHARE } } },
      { at_tick: tick + end + REPLAY_SPREAD_TICKS, op: 'dry_all' },
    );
    tick += end + REPLAY_SPREAD_TICKS + 1;
  });
  events.push({ at_tick: tick, op: { settle: { share: LIVE_SETTLE_SHARE } } });
  events.sort((a, b) => a.at_tick - b.at_tick);

  return {
    sceneJson: JSON.stringify({
      ...doc,
      id: `packing-suitcase-${seed}-${packed.length}`,
      timeline: { total_ticks: tick + REPLAY_TAIL_TICKS, events },
    }),
    painter,
  };
}
