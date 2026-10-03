/**
 * One brush stroke fitted to the clear space on a surface.
 *
 * The surface's text and icons are obstacles; everything else, including the
 * paper just past the surface edge, is open. The stroke takes the longest and
 * fattest straight corridor through that space. It is bowed a little and
 * tapered from a fat end to a thin one, and may flick droplets off the fat end.
 * Everything is in surface pixels with the origin at the surface's top-left;
 * a coordinate outside `0..cw` x `0..ch` is paint that bleeds off the edge.
 */

export interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface Pt {
  x: number;
  y: number;
}

export interface Dab {
  x: number;
  y: number;
  r: number;
  /** Charged with water first so it blooms like a drop; a flicked droplet dries with an edge. */
  wet: boolean;
}

/** The surface edge a stroke hugs; `free` floats in the interior. */
export type StrokeSide = 'top' | 'bottom' | 'left' | 'right' | 'free';

export interface DropStroke {
  /** Centreline, fat end first. */
  path: Pt[];
  /** Brush radius at the fat end and at the thin end. */
  radius: [number, number];
  spatter: Dab[];
  side: StrokeSide;
  /** Everything the paint can reach, including the radii. */
  bounds: Box;
}

export interface StrokeOptions {
  /** Varies bow, taper, length and spatter; a run passes a different one per member. */
  seed?: number;
  /**
   * Which of the tied corridors to take, counted round from the best. A run
   * passes its member's position, so neighbours take different edges by rule
   * rather than by chance.
   */
  turn?: number;
  step?: number;
  spatter?: boolean;
}

/**
 * Widest the fat end may be, as a fraction of the short edge. Anything wider
 * on a squarish surface reads as a disc with a boundary rather than a mark.
 */
export const MAX_THICKNESS = 0.42;
/**
 * Widest the fat end may be on any surface, in pixels. A brush is a physical
 * thing: the same accent brush paints a card and a hero, and a 330 px square
 * given 42 % of its edge got a wave, not a stroke.
 */
export const MAX_RADIUS = 18;
/** A free stroke is held thinner than an edge one: only fat free marks blob. */
const FREE_THICKNESS = 0.6;
/** A stroke shorter than this many of its own widths is a dab, not a stroke. */
const MIN_ASPECT = 3;
/** Share of the surface the ink footprint may cover. */
const MAX_FOOTPRINT = 0.2;
/** Thinnest brush that still reads at surface scale. */
const MIN_RADIUS = 5;
/** Share of the brush that has to land on the surface; less is a stain along the border. */
const MIN_VISIBLE = 0.4;
/** Clearance a corridor sample needs before it counts as open. */
const MIN_CLEAR = 7;
/** How far off the surface the search and the paint may go, as a fraction of the short edge. */
const BLEED = 0.3;
/**
 * Scores a corridor whose brush crosses a surface edge above one that floats.
 * Marks flush to an edge have never failed a review; free discs did.
 */
const EDGE_BIAS = 0.35;
/** Corridors this close to the best in score are interchangeable, and the seed picks. */
const TIE = 0.12;
/** The brush keeps this share of the measured clearance so a soft edge never touches text. */
const CLEAR_MARGIN = 0.9;
const ANGLES = [0, 22.5, 45, 67.5, 90, 112.5, 135, 157.5];

/**
 * A 32-bit hash of the seed and a purpose index with full avalanche, so seeds
 * a few apart still differ in every bit. A single xorshift does not: seeds
 * `0..7` come out identical in their top bits, which is where a unit value's
 * magnitude lives.
 */
function hash32(seed: number, index: number): number {
  let h = (seed + Math.imul(index + 1, 0x9e3779b9)) >>> 0;
  h = Math.imul(h ^ (h >>> 16), 0x85ebca6b) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35) >>> 0;
  h = Math.imul(h ^ (h >>> 16), 0x27d4eb2f) >>> 0;
  return (h ^ (h >>> 15)) >>> 0;
}

/** A unit value in `0..1` from the seed and a purpose index. */
function unit(seed: number, index: number): number {
  return hash32(seed, index) / 0x100000000;
}

function between(seed: number, index: number, lo: number, hi: number): number {
  return lo + unit(seed, index) * (hi - lo);
}

/**
 * The grid step for a surface of this size. The corridor sweep costs the
 * area over the step squared. A few pixels of slack in where a stroke a
 * hundred pixels long sits cannot be seen.
 */
export function strokeStep(cw: number, ch: number): number {
  return Math.min(12, Math.max(4, Math.round(Math.min(cw, ch) / 16)));
}

export function bleedFor(cw: number, ch: number): number {
  return Math.min(48, Math.max(8, Math.round(Math.min(cw, ch) * BLEED)));
}

function clearanceAt(x: number, y: number, boxes: readonly Box[], cap: number): number {
  let best = cap;
  for (const b of boxes) {
    const dx = Math.max(b.x - x, 0, x - (b.x + b.w));
    const dy = Math.max(b.y - y, 0, y - (b.y + b.h));
    const d = Math.hypot(dx, dy);
    if (d === 0) return 0;
    if (d < best) best = d;
  }
  return best;
}

/**
 * Distance from every grid point to the nearest obstacle, over the surface
 * and its bleed margin. Read with bilinear interpolation, so a chord's score
 * does not depend on where it falls between grid rows; a query outside the
 * grid is clamped to its edge.
 */
export interface ClearanceField {
  readonly x0: number;
  readonly y0: number;
  readonly step: number;
  readonly cols: number;
  readonly rows: number;
  at(x: number, y: number): number;
}

export function clearanceField(
  cw: number,
  ch: number,
  obstacles: readonly Box[],
  { step = strokeStep(cw, ch), margin = bleedFor(cw, ch) } = {},
): ClearanceField {
  const x0 = -margin;
  const y0 = -margin;
  const cols = Math.floor((cw + 2 * margin) / step) + 1;
  const rows = Math.floor((ch + 2 * margin) / step) + 1;
  // With nothing in the way the field is flat; the cap keeps it finite.
  const cap = Math.max(cw, ch) + 2 * margin;
  const clear = new Float32Array(cols * rows);
  for (let iy = 0; iy < rows; iy++) {
    for (let ix = 0; ix < cols; ix++) {
      clear[iy * cols + ix] = clearanceAt(x0 + ix * step, y0 + iy * step, obstacles, cap);
    }
  }
  return {
    x0,
    y0,
    step,
    cols,
    rows,
    at(x, y) {
      const fx = Math.min(cols - 1, Math.max(0, (x - x0) / step));
      const fy = Math.min(rows - 1, Math.max(0, (y - y0) / step));
      const ix = Math.min(cols - 2, Math.floor(fx));
      const iy = Math.min(rows - 2, Math.floor(fy));
      const tx = fx - ix;
      const ty = fy - iy;
      const c00 = clear[iy * cols + ix]!;
      const c10 = clear[iy * cols + ix + 1] ?? c00;
      const c01 = clear[(iy + 1) * cols + ix] ?? c00;
      const c11 = clear[(iy + 1) * cols + ix + 1] ?? c10;
      return (c00 * (1 - tx) + c10 * tx) * (1 - ty) + (c01 * (1 - tx) + c11 * tx) * ty;
    },
  };
}

interface Sample {
  x: number;
  y: number;
  /** Distance along the corridor. */
  s: number;
  /** Measured clearance. */
  c: number;
}

interface Run {
  dir: Pt;
  samples: Sample[];
  side: StrokeSide;
  score: number;
}

/** Signed distance to the nearest surface edge; negative off the surface. */
function inset(x: number, y: number, cw: number, ch: number): number {
  return Math.min(x, y, cw - x, ch - y);
}

function nearestSide(x: number, y: number, cw: number, ch: number): Exclude<StrokeSide, 'free'> {
  const d = [
    ['top', y],
    ['bottom', ch - y],
    ['left', x],
    ['right', cw - x],
  ] as const;
  let best: (typeof d)[number] = d[0];
  for (const e of d) if (e[1] < best[1]) best = e;
  return best[0];
}

/** How much of a brush of radius `r` centred `e` inside the edge is on the surface. */
function visible(e: number, r: number): number {
  return Math.min(1, Math.max(0, (e + r) / (2 * r)));
}

/**
 * The value of laying brush at one corridor sample. It is the visible width
 * the brush would paint, raised by the edge bias when it crosses off the surface.
 */
function sampleValue(sm: Sample, cw: number, ch: number, cap: number): number {
  const r = Math.min(sm.c * CLEAR_MARGIN, cap);
  const e = inset(sm.x, sm.y, cw, ch);
  const v = r * visible(e, r);
  return e < r ? v * (1 + EDGE_BIAS) : v;
}

/**
 * The longest, fattest clear straight line on the surface, per side.
 *
 * Every angle in {@link ANGLES} is swept with parallel chords a step apart.
 * Each chord is cut into runs of open samples. A run is scored by the visible
 * brush width it could carry. The best run per side and the best floating one
 * both come back. A caller can then choose among near-ties instead of always
 * taking the same edge.
 */
/**
 * Chord spacing and sample spacing of the sweep, as a multiple of the field's
 * step. The field stays fine for the radius fit; the sweep only has to find
 * the corridor, and a stroke a hundred pixels long does not care which of two
 * chords six pixels apart it took.
 */
const SWEEP = 1.5;

function corridors(field: ClearanceField, cw: number, ch: number, margin: number): Run[] {
  const step = field.step * SWEEP;
  const rCap = Math.min(MAX_RADIUS, (Math.min(cw, ch) * MAX_THICKNESS) / 2);
  const corners: Pt[] = [
    { x: -margin, y: -margin },
    { x: cw + margin, y: -margin },
    { x: -margin, y: ch + margin },
    { x: cw + margin, y: ch + margin },
  ];
  const inside = (x: number, y: number) => x >= -margin && x <= cw + margin && y >= -margin && y <= ch + margin;

  const best = new Map<StrokeSide, Run>();
  const consider = (dir: Pt, samples: Sample[]) => {
    if (samples.length < 2) return;
    const mid = samples[Math.floor(samples.length / 2)]!;
    const eMid = inset(mid.x, mid.y, cw, ch);
    const side: StrokeSide = eMid < rCap ? nearestSide(mid.x, mid.y, cw, ch) : 'free';
    const cap = side === 'free' ? rCap * FREE_THICKNESS : rCap;
    let score = 0;
    for (const sm of samples) score += sampleValue(sm, cw, ch, cap) * step;
    const prior = best.get(side);
    if (!prior || score > prior.score) best.set(side, { dir, samples, side, score });
  };

  for (const deg of ANGLES) {
    const a = (deg * Math.PI) / 180;
    const dir = { x: Math.cos(a), y: Math.sin(a) };
    const nrm = { x: -dir.y, y: dir.x };
    let oMin = Infinity;
    let oMax = -Infinity;
    let sMin = Infinity;
    let sMax = -Infinity;
    for (const c of corners) {
      const o = c.x * nrm.x + c.y * nrm.y;
      const s = c.x * dir.x + c.y * dir.y;
      if (o < oMin) oMin = o;
      if (o > oMax) oMax = o;
      if (s < sMin) sMin = s;
      if (s > sMax) sMax = s;
    }
    for (let o = oMin + step / 2; o <= oMax; o += step) {
      let run: Sample[] = [];
      for (let s = sMin; s <= sMax; s += step) {
        const x = o * nrm.x + s * dir.x;
        const y = o * nrm.y + s * dir.y;
        const c = inside(x, y) ? field.at(x, y) : 0;
        if (c >= MIN_CLEAR) {
          run.push({ x, y, s, c });
        } else if (run.length) {
          consider(dir, run);
          run = [];
        }
      }
      if (run.length) consider(dir, run);
    }
  }
  return [...best.values()].sort((p, q) => q.score - p.score);
}

/** A quadratic curve from `a` to `b` whose midpoint is `sagitta` off the chord along `nrm`. */
function bowed(a: Pt, b: Pt, nrm: Pt, sagitta: number, n: number): Pt[] {
  const cx = (a.x + b.x) / 2 + nrm.x * sagitta * 2;
  const cy = (a.y + b.y) / 2 + nrm.y * sagitta * 2;
  const out: Pt[] = [];
  for (let i = 0; i < n; i++) {
    const t = i / (n - 1);
    const u = 1 - t;
    out.push({
      x: u * u * a.x + 2 * u * t * cx + t * t * b.x,
      y: u * u * a.y + 2 * u * t * cy + t * t * b.y,
    });
  }
  return out;
}

function arcLength(path: readonly Pt[]): number {
  let out = 0;
  for (let i = 1; i < path.length; i++) out += Math.hypot(path[i]!.x - path[i - 1]!.x, path[i]!.y - path[i - 1]!.y);
  return out;
}

/** How much of the brush along `path` lands on the surface, summed over its points. */
function pathVisible(path: readonly Pt[], r0: number, k: number, cw: number, ch: number): number {
  let out = 0;
  const last = path.length - 1;
  path.forEach((p, i) => {
    const r = r0 * (1 - (1 - k) * (i / last));
    out += visible(inset(p.x, p.y, cw, ch), r);
  });
  return out;
}

/** The largest fat-end radius whose linear taper to `k` of itself stays under the clearance everywhere. */
function fitRadius(path: readonly Pt[], field: ClearanceField, k: number, cap: number): number {
  let r0 = cap;
  const last = path.length - 1;
  path.forEach((p, i) => {
    const t = i / last;
    const allowed = (field.at(p.x, p.y) * CLEAR_MARGIN) / (1 - (1 - k) * t);
    if (allowed < r0) r0 = allowed;
  });
  return r0;
}

/**
 * Droplets flicked off the fat end: a handful of small dabs around the head,
 * smaller with distance, each on the surface and on clear paper. A surface
 * too small or a stroke too thin gets none.
 */
function spatterFor(
  path: readonly Pt[],
  r0: number,
  field: ClearanceField,
  cw: number,
  ch: number,
  seed: number,
): Dab[] {
  if (Math.min(cw, ch) < 80 || r0 < 8) return [];
  const head = path[0]!;
  const next = path[1]!;
  const travel = Math.atan2(next.y - head.y, next.x - head.x);
  const want = 3 + Math.floor(unit(seed, 40) * 5);
  const out: Dab[] = [];
  for (let i = 0; i < 40 && out.length < want; i++) {
    // Anywhere around the head except along the stroke itself.
    const ang = travel + Math.PI + between(seed, 50 + i * 3, -2.4, 2.4);
    const dist = between(seed, 51 + i * 3, 1.3, 3.6) * r0;
    const x = head.x + Math.cos(ang) * dist;
    const y = head.y + Math.sin(ang) * dist;
    const falloff = 1 - (dist / r0 - 1.3) / 2.3;
    const r = Math.max(1.5, r0 * between(seed, 52 + i * 3, 0.08, 0.22) * (0.5 + 0.5 * falloff));
    if (inset(x, y, cw, ch) < 0) continue;
    if (field.at(x, y) * CLEAR_MARGIN < r + 2) continue;
    if (out.some((d) => Math.hypot(d.x - x, d.y - y) < d.r + r + 2)) continue;
    out.push({ x, y, r, wet: false });
  }
  return out;
}

/**
 * Fits the stroke to a surface. `null` when nothing worth painting fits: the
 * content covers the surface, or the widest clear line is a dab.
 */
export function fitStroke(
  cw: number,
  ch: number,
  obstacles: readonly Box[],
  { seed = 0, turn = 0, step = strokeStep(cw, ch), spatter = true }: StrokeOptions = {},
): DropStroke | null {
  if (cw <= 0 || ch <= 0) return null;
  const margin = bleedFor(cw, ch);
  const field = clearanceField(cw, ch, obstacles, { step, margin });
  const runs = corridors(field, cw, ch, margin);
  if (runs.length === 0) return null;

  // Near-ties are interchangeable, so a run of identical surfaces does not
  // all take the same edge. A tied corridor that cannot carry a stroke hands
  // over to the next, so the turn decides where a stroke goes, never whether.
  const top = runs[0]!.score;
  const ties = runs.filter((r) => r.score >= top * (1 - TIE));
  const pick = turn % ties.length;
  for (let i = 0; i < ties.length; i++) {
    const fitted = fitRun(ties[(pick + i) % ties.length]!, field, cw, ch, seed, spatter);
    if (fitted) return fitted;
  }
  return null;
}

function fitRun(
  run: Run,
  field: ClearanceField,
  cw: number,
  ch: number,
  seed: number,
  spatter: boolean,
): DropStroke | null {
  const step = field.step;
  const rCap = Math.min(MAX_RADIUS, (Math.min(cw, ch) * MAX_THICKNESS) / 2) * (run.side === 'free' ? FREE_THICKNESS : 1);

  // The window along the run with the most visible brush in it, nudged by
  // the seed within the slack the run leaves.
  const values = run.samples.map((sm) => sampleValue(sm, cw, ch, rCap));
  const runLen = run.samples[run.samples.length - 1]!.s - run.samples[0]!.s;
  const frac = between(seed, 2, 0.45, 0.7);
  const want = Math.max(1, Math.round((frac * runLen) / step));
  const n = Math.min(want, run.samples.length - 1);
  let bestAt = 0;
  let bestSum = -1;
  let sum = 0;
  for (let i = 0; i <= n; i++) sum += values[i]!;
  for (let i = 0; i + n < run.samples.length; i++) {
    if (i > 0) sum += values[i + n]! - values[i - 1]!;
    if (sum > bestSum) {
      bestSum = sum;
      bestAt = i;
    }
  }
  const slack = run.samples.length - 1 - n;
  // Whether the surface carries a stroke at all is judged on the best window
  // and never on the jitter, so a run of identical surfaces either all draw or
  // none do. The jitter then only moves within windows nearly as visible.
  const windowVisible = (i: number) => {
    let v = 0;
    for (let j = i; j <= i + n; j++) {
      const sm = run.samples[j]!;
      v += visible(inset(sm.x, sm.y, cw, ch), rCap);
    }
    return v / (n + 1);
  };
  const bestVisible = windowVisible(bestAt);
  if (bestVisible < MIN_VISIBLE) return null;
  let at = Math.min(Math.max(0, bestAt + Math.round((unit(seed, 3) - 0.5) * 0.3 * slack)), slack);
  while (at !== bestAt && windowVisible(at) < bestVisible * 0.9) at += at < bestAt ? 1 : -1;
  let a = run.samples[at]!;
  let b = run.samples[at + n]!;

  // The fat end goes where there is more room; a close call is the seed's.
  const third = Math.max(1, Math.floor(n / 3));
  let roomA = 0;
  let roomB = 0;
  for (let i = 0; i < third; i++) {
    roomA += run.samples[at + i]!.c;
    roomB += run.samples[at + n - i]!.c;
  }
  const swap = roomB > roomA * 1.15 || (roomB > roomA * 0.87 && unit(seed, 4) < 0.5);
  if (swap) [a, b] = [b, a];

  const dir = { x: b.x - a.x, y: b.y - a.y };
  const L = Math.hypot(dir.x, dir.y);
  if (L === 0) return null;
  dir.x /= L;
  dir.y /= L;
  const nrm = { x: -dir.y, y: dir.x };
  const k = between(seed, 5, 0.15, 0.32);
  const points = Math.min(24, Math.max(6, Math.round(L / 8)));

  // The bow prefers the roomier side, and turns toward the surface when the
  // brush is already crossing its edge, because bowing off it only hides
  // paint. A bow that costs much radius is flattened rather than kept.
  const fullSagitta = L * between(seed, 6, 0.08, 0.18);
  const mx = (a.x + b.x) / 2;
  const my = (a.y + b.y) / 2;
  const plus = field.at(mx + nrm.x * fullSagitta, my + nrm.y * fullSagitta);
  const minus = field.at(mx - nrm.x * fullSagitta, my - nrm.y * fullSagitta);
  const eMid = inset(mx, my, cw, ch);
  let preferred: 1 | -1;
  if (eMid < rCap * 0.5) {
    preferred = inset(mx + nrm.x, my + nrm.y, cw, ch) > eMid ? 1 : -1;
  } else if (plus > minus * 1.2) {
    preferred = 1;
  } else if (minus > plus * 1.2) {
    preferred = -1;
  } else {
    preferred = unit(seed, 7) < 0.5 ? 1 : -1;
  }
  let path = bowed(a, b, nrm, 0, points);
  const straight = fitRadius(path, field, k, rCap);
  const straightVisible = pathVisible(path, straight, k, cw, ch);
  let sagitta = 0;
  let r0 = straight;
  for (const scale of [1, 0.5, 0.25]) {
    for (const sign of [preferred, -preferred]) {
      const candidate = bowed(a, b, nrm, fullSagitta * scale * sign, points);
      const fitted = fitRadius(candidate, field, k, rCap);
      if (fitted >= straight * 0.85 && pathVisible(candidate, fitted, k, cw, ch) >= straightVisible * 0.85) {
        sagitta = fullSagitta * scale * sign;
        path = candidate;
        r0 = fitted;
        break;
      }
    }
    if (sagitta !== 0) break;
  }

  // Footprint: shorten before thinning, and never below a stroke's proportions.
  const maxArea = MAX_FOOTPRINT * cw * ch;
  let length = arcLength(path);
  const shorten = (target: number) => {
    if (target >= length) return;
    // The fat end stays; the thin end is pulled in.
    const cut = target / length;
    b = { x: a.x + (b.x - a.x) * cut, y: a.y + (b.y - a.y) * cut } as Sample;
    sagitta *= cut;
    path = bowed(a, b, nrm, sagitta, points);
    r0 = Math.min(r0, fitRadius(path, field, k, rCap));
    length = arcLength(path);
  };
  if (length * r0 * (1 + k) > maxArea) {
    shorten(Math.max(MIN_ASPECT * 2 * r0, maxArea / (r0 * (1 + k))));
    if (length * r0 * (1 + k) > maxArea) {
      // Shortening stopped at the minimum aspect, so the brush thins to the
      // widest a stroke of that length may be, and the length follows it.
      r0 = Math.min(r0, Math.sqrt(maxArea / (MIN_ASPECT * 2 * (1 + k))));
      shorten(MIN_ASPECT * 2 * r0);
    }
  }
  if (length < MIN_ASPECT * 2 * r0) r0 = length / (MIN_ASPECT * 2);
  if (r0 < MIN_RADIUS) return null;
  if (pathVisible(path, r0, k, cw, ch) < MIN_VISIBLE * path.length) return null;
  const r1 = r0 * k;

  const dabs = spatter ? spatterFor(path, r0, field, cw, ch, seed) : [];

  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  const grow = (x: number, y: number, r: number) => {
    if (x - r < x0) x0 = x - r;
    if (y - r < y0) y0 = y - r;
    if (x + r > x1) x1 = x + r;
    if (y + r > y1) y1 = y + r;
  };
  path.forEach((p, i) => grow(p.x, p.y, r0 + (r1 - r0) * (i / (path.length - 1))));
  for (const d of dabs) grow(d.x, d.y, d.r);

  return {
    path,
    radius: [r0, r1],
    spatter: dabs,
    side: run.side,
    bounds: { x: x0, y: y0, w: x1 - x0, h: y1 - y0 },
  };
}

/** Which gesture a surface gets. */
export type DropKind = 'stroke' | 'drops' | 'splotch' | 'border' | 'wash' | 'glaze';

/**
 * Everything a surface paints, in surface pixels. A `stroke` is one stroke
 * and its flicked spatter. `drops` are two or three wet dabs and no stroke. A
 * `splotch` is one tall blob spilling off the top and bottom. A `border` runs
 * the perimeter. A `wash` is one broad stroke over the whole surface, lifted
 * back out of its copy. A `glaze` is the same stroke laid thin over the whole
 * control, label included.
 */
export interface DropMark {
  kind: DropKind;
  strokes: { path: Pt[]; radius: [number, number]; side: StrokeSide }[];
  dabs: Dab[];
  bounds: Box;
  /**
   * Boxes the paint is lifted back out of once it lands, so a mark laid
   * over the whole control keeps off its copy.
   */
  reserves?: readonly Box[];
}

export interface MarkOptions extends StrokeOptions {
  /** `auto` cycles stroke, drops, splotch and border along a run by `turn`. */
  kind?: DropKind | 'auto';
}

/** Largest drop, in pixels; above this a round wash reads as a disc with a boundary. */
export const MAX_DROP_RADIUS = 20;
const MIN_DROP_RADIUS = 8;
/** Drops keep this many radii between their centres, so two never merge into a figure of eight. */
const DROP_SPACING = 2.6;
/** A drop hanging off an edge keeps at least this much of itself on the surface. */
const DROP_VISIBLE = 0.6;

function boundsOf(strokes: DropMark['strokes'], dabs: readonly Dab[]): Box {
  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  const grow = (x: number, y: number, r: number) => {
    if (x - r < x0) x0 = x - r;
    if (y - r < y0) y0 = y - r;
    if (x + r > x1) x1 = x + r;
    if (y + r > y1) y1 = y + r;
  };
  for (const st of strokes) {
    const [r0, r1] = st.radius;
    st.path.forEach((p, i) => grow(p.x, p.y, r0 + (r1 - r0) * (i / Math.max(1, st.path.length - 1))));
  }
  for (const d of dabs) grow(d.x, d.y, d.r);
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

/**
 * Two or three round drops in the clear space, largest first.
 *
 * Each takes the roomiest point left, valued like a corridor sample: the
 * visible radius it could have, with the edge bias. Drops therefore sit
 * against an edge or a corner before they float. The sizes step down, so the
 * three read as one splash rather than a row of coins. `null` when fewer than
 * two fit.
 */
export function fitDrops(
  cw: number,
  ch: number,
  obstacles: readonly Box[],
  { seed = 0, turn = 0, step = strokeStep(cw, ch) }: StrokeOptions = {},
): Dab[] | null {
  if (cw <= 0 || ch <= 0) return null;
  const margin = bleedFor(cw, ch);
  const field = clearanceField(cw, ch, obstacles, { step, margin });
  const rCap = Math.min(MAX_DROP_RADIUS, (Math.min(cw, ch) * MAX_THICKNESS) / 2);
  const want = 2 + (hash32(seed, 60) % 2);
  const out: Dab[] = [];
  const scale = [1, between(seed, 61, 0.55, 0.75), between(seed, 62, 0.4, 0.55)];
  // The first drop favours a different corner on each turn of a run.
  const corner = hash32(turn, 63) % 4;
  for (let n = 0; n < want; n++) {
    const cap = rCap * scale[n]!;
    if (cap < MIN_DROP_RADIUS) break;
    let best: { x: number; y: number; r: number; v: number } | null = null;
    for (let iy = 0; iy < field.rows; iy++) {
      for (let ix = 0; ix < field.cols; ix++) {
        const x = field.x0 + ix * step;
        const y = field.y0 + iy * step;
        const r = Math.min(field.at(x, y) * CLEAR_MARGIN, cap);
        if (r < MIN_DROP_RADIUS) continue;
        const e = inset(x, y, cw, ch);
        if (visible(e, r) < DROP_VISIBLE) continue;
        let clear = true;
        for (const d of out) {
          if (Math.hypot(d.x - x, d.y - y) < (d.r + r) * (DROP_SPACING / 2)) {
            clear = false;
            break;
          }
        }
        if (!clear) continue;
        let v = r * visible(e, r) * (e < r ? 1 + EDGE_BIAS : 1);
        if (n === 0) {
          const towardX = corner % 2 === 0 ? 1 - x / cw : x / cw;
          const towardY = corner < 2 ? 1 - y / ch : y / ch;
          v *= 1 + 0.3 * Math.max(0, Math.min(1, (towardX + towardY) / 2));
        }
        if (!best || v > best.v) best = { x, y, r, v };
      }
    }
    if (!best) break;
    // Off the grid a little, so a run does not put every drop on the same cell.
    const jx = (unit(seed, 64 + n) - 0.5) * step;
    const jy = (unit(seed, 68 + n) - 0.5) * step;
    const r = Math.min(best.r, field.at(best.x + jx, best.y + jy) * CLEAR_MARGIN);
    out.push({ x: best.x + jx, y: best.y + jy, r, wet: true });
  }
  return out.length >= 2 ? out : null;
}

/**
 * Gap, in pixels, under which two reserved boxes become one. The boxes arrive
 * padded, so copy within about twice this of other copy shares a reserve.
 */
export const RESERVE_MERGE_GAP = 16;

/**
 * The copy's boxes grown into whole blocks.
 *
 * Reserved one line at a time, the paint runs into the leading between lines
 * and the gutter beside an icon, and the copy reads as cut out of a wash that
 * got there first. Boxes closer than `gap` join, chains included, so a block
 * of lines and its glyph clear as one; each block is reserved as its bounds.
 * This is a closing of the union by half the gap, exact for boxes stacked
 * along a line and generous for an L-shaped block. A block that comes within
 * `gap` of the surface's edge runs out past it by `bleed`, so no sliver of
 * paint is left between the copy and the border.
 */
export function mergeReserves(
  boxes: readonly Box[],
  surface?: { cw: number; ch: number; bleed: number },
  gap = RESERVE_MERGE_GAP,
): Box[] {
  const parent = boxes.map((_, i) => i);
  const find = (i: number): number => (parent[i] === i ? i : (parent[i] = find(parent[i]!)));
  const apart = (a: Box, b: Box) =>
    Math.max(a.x - (b.x + b.w), b.x - (a.x + a.w), a.y - (b.y + b.h), b.y - (a.y + a.h));
  for (let i = 0; i < boxes.length; i++) {
    for (let j = i + 1; j < boxes.length; j++) {
      if (apart(boxes[i]!, boxes[j]!) < gap) parent[find(i)] = find(j);
    }
  }
  const blocks = new Map<number, Box>();
  boxes.forEach((b, i) => {
    const root = find(i);
    const at = blocks.get(root);
    if (!at) {
      blocks.set(root, { ...b });
      return;
    }
    const x = Math.min(at.x, b.x);
    const y = Math.min(at.y, b.y);
    blocks.set(root, {
      x,
      y,
      w: Math.max(at.x + at.w, b.x + b.w) - x,
      h: Math.max(at.y + at.h, b.y + b.h) - y,
    });
  });
  if (!surface) return [...blocks.values()];
  const { cw, ch, bleed } = surface;
  return [...blocks.values()].map((b) => {
    const x0 = b.x < gap ? -bleed : b.x;
    const y0 = b.y < gap ? -bleed : b.y;
    const x1 = b.x + b.w > cw - gap ? cw + bleed : b.x + b.w;
    const y1 = b.y + b.h > ch - gap ? ch + bleed : b.y + b.h;
    return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
  });
}

/**
 * One brush wider than the surface is tall, run off both ends, so its dried
 * edge falls outside and the eye reads paint rather than a shape laid on.
 */
function broadStroke(cw: number, ch: number, seed: number): DropMark['strokes'] {
  const margin = bleedFor(cw, ch);
  const r = ch * 0.62;
  const y0 = ch * (0.5 + (unit(seed, 70) - 0.5) * 0.16);
  const y1 = ch * (0.5 + (unit(seed, 71) - 0.5) * 0.16);
  const a = { x: -margin, y: y0 };
  const b = { x: cw + margin, y: y1 };
  const L = Math.hypot(b.x - a.x, b.y - a.y);
  const sagitta = L * between(seed, 72, -0.05, 0.05);
  const path = bowed(a, b, { x: 0, y: 1 }, sagitta, Math.min(16, Math.max(6, Math.round(L / 12))));
  return [{ path, radius: [r, r * 0.9], side: 'free' }];
}

/**
 * A broad stroke over the whole surface with its copy reserved: the copy's
 * boxes are merged into blocks and the paint is lifted back out of them, so it
 * never sits under a word.
 */
export function washStroke(cw: number, ch: number, seed = 0, obstacles: readonly Box[] = []): DropMark {
  const strokes = broadStroke(cw, ch, seed);
  const reserves = mergeReserves(obstacles, { cw, ch, bleed: bleedFor(cw, ch) });
  return { kind: 'wash', strokes, dabs: [], bounds: boundsOf(strokes, []), reserves };
}

/**
 * The broad stroke laid thin over the whole control, label included, for a
 * surface that is all label: a button has no open paper for a wash to keep
 * to, so the label sits on the paint and reads through it.
 */
export function glazeStroke(cw: number, ch: number, seed = 0): DropMark {
  const strokes = broadStroke(cw, ch, seed);
  return { kind: 'glaze', strokes, dabs: [], bounds: boundsOf(strokes, []) };
}

/** Brush of a border, as a fraction of the short edge, and its pixel bounds. */
const BORDER_WEIGHT = 0.06;
const BORDER_MIN = 5;
const BORDER_MAX = 9;
/** How far round the perimeter a border is drawn; the gap is what makes it look drawn. */
const BORDER_CLOSE = 0.94;
/**
 * A splotch's radius, as a fraction of the surface height. Well past half, so
 * the circle leaves the paper above and below and only its curve toward the
 * copy stays in view.
 */
const SPLOTCH_RADIUS = 0.9;
/** Narrowest clear band beside the copy a splotch will fill, in pixels. */
const SPLOTCH_MIN_BAND = 24;

/**
 * A thin stroke round the whole perimeter, drawn like a frame. It starts
 * somewhere along the top and runs clockwise with a hand's wobble round
 * softened corners. It thins as it goes and stops short of where it began.
 * Its centreline sits just outside the edge, so the brush overlaps the card
 * and bleeds off it while the dried edge shows on the inside.
 */
export function borderMark(cw: number, ch: number, seed = 0): DropMark {
  const short = Math.min(cw, ch);
  const r0 = Math.min(BORDER_MAX, Math.max(BORDER_MIN, short * BORDER_WEIGHT));
  // Just outside the edge, so the brush overlaps it and bleeds off the card.
  const inset = -r0 * 0.2;
  const rc = Math.min(14, Math.max(6, short * 0.08));
  const x0 = inset;
  const y0 = inset;
  const x1 = cw - inset;
  const y1 = ch - inset;
  // The rounded rectangle as a closed polyline, then sampled by arc length.
  const corner = (cx: number, cy: number, from: number): Pt[] =>
    Array.from({ length: 5 }, (_, i) => {
      const a = from + (i / 4) * (Math.PI / 2);
      return { x: cx + rc * Math.cos(a), y: cy + rc * Math.sin(a) };
    });
  const loop: Pt[] = [
    { x: x0 + rc, y: y0 },
    { x: x1 - rc, y: y0 },
    ...corner(x1 - rc, y0 + rc, -Math.PI / 2),
    { x: x1, y: y1 - rc },
    ...corner(x1 - rc, y1 - rc, 0),
    { x: x0 + rc, y: y1 },
    ...corner(x0 + rc, y1 - rc, Math.PI / 2),
    { x: x0, y: y0 + rc },
    ...corner(x0 + rc, y0 + rc, Math.PI),
  ];
  const seg: number[] = [0];
  for (let i = 1; i <= loop.length; i++) {
    const a = loop[i - 1]!;
    const b = loop[i % loop.length]!;
    seg.push(seg[i - 1]! + Math.hypot(b.x - a.x, b.y - a.y));
  }
  const total = seg[loop.length]!;
  const at = (d: number): Pt => {
    const s = ((d % total) + total) % total;
    let i = 1;
    while (i < seg.length && seg[i]! < s) i++;
    const a = loop[i - 1]!;
    const b = loop[i % loop.length]!;
    const t = (s - seg[i - 1]!) / Math.max(1e-6, seg[i]! - seg[i - 1]!);
    return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
  };
  // Start along the top run, so the gap always falls where the eye starts.
  const start = between(seed, 80, 0.15, 0.6) * (x1 - x0 - 2 * rc);
  const n = Math.min(96, Math.max(32, Math.round(total / 10)));
  const wobble = between(seed, 81, 0.8, 1.6);
  const phase = between(seed, 82, 0, Math.PI * 2);
  const path: Pt[] = [];
  for (let i = 0; i <= n; i++) {
    const d = start + (i / n) * total * BORDER_CLOSE;
    const p = at(d);
    const q = at(d + 1);
    // Wobble across the line, along its normal.
    const len = Math.hypot(q.x - p.x, q.y - p.y) || 1;
    const nx = -(q.y - p.y) / len;
    const ny = (q.x - p.x) / len;
    const w = Math.sin(phase + (i / n) * Math.PI * 7) * wobble;
    path.push({ x: p.x + nx * w, y: p.y + ny * w });
  }
  const strokes: DropMark['strokes'] = [{ path, radius: [r0, r0 * 0.5], side: 'free' }];
  return { kind: 'border', strokes, dabs: [], bounds: boundsOf(strokes, []) };
}

/**
 * One big circle filling the clear band beside the copy. Its radius comes from
 * the surface height, not the band, so it leaves the paper above and below and
 * usually past the side too; what stays in view is the curve toward the copy,
 * a bracket of paint holding the text. The band is whichever side of the copy
 * is clearer, the wider one when both are. `null` when neither is wide enough.
 */
export function fitSplotch(
  cw: number,
  ch: number,
  obstacles: readonly Box[],
  { seed = 0, turn = 0, step = strokeStep(cw, ch) }: StrokeOptions = {},
): DropMark | null {
  if (cw <= 0 || ch <= 0) return null;
  const margin = bleedFor(cw, ch);
  const field = clearanceField(cw, ch, obstacles, { step, margin });
  // A column is open when nothing sits in it down the whole height.
  const open = (x: number) => {
    for (let y = 0; y <= ch; y += step) if (field.at(x, y) < step) return false;
    return true;
  };
  // The open band at each side, measured in from the edge.
  let right = 0;
  while (right < cw && open(cw - right - step / 2)) right += step;
  let left = 0;
  while (left < cw && open(left + step / 2)) left += step;
  if (right >= cw || left >= cw) {
    // Nothing in the way: the splotch would flood the surface.
    return null;
  }
  if (Math.max(left, right) < SPLOTCH_MIN_BAND) return null;
  // A near-tie between the sides goes the way the turn says.
  const side = right === left ? (turn % 2 === 0 ? 'right' : 'left') : right > left ? 'right' : 'left';
  const band = side === 'right' ? right : left;
  const r = ch * SPLOTCH_RADIUS * between(seed, 90, 0.92, 1.08);
  // The circle's edge lands a little inside the band, so the paper between it
  // and the copy is what reads as the gap.
  const gap = Math.min(band * 0.2, step);
  const x = side === 'right' ? cw - band + gap + r : band - gap - r;
  const y = ch * (0.5 + (unit(seed, 91) - 0.5) * 0.12);
  const dabs: Dab[] = [{ x, y, r, wet: true }];
  return { kind: 'splotch', strokes: [], dabs, bounds: boundsOf([], dabs) };
}

/**
 * The mark for a surface. `auto` cycles the gestures by `turn`, so a run reads
 * as a composition rather than as one effect stamped down a column. A gesture
 * that does not fit hands over to a stroke, then to drops; only when nothing
 * fits is nothing drawn.
 */
const CYCLE: readonly DropKind[] = ['stroke', 'drops', 'splotch', 'border'];

export function fitMark(
  cw: number,
  ch: number,
  obstacles: readonly Box[],
  { kind = 'auto', seed = 0, turn = 0, step = strokeStep(cw, ch), spatter = true }: MarkOptions = {},
): DropMark | null {
  if (kind === 'wash') return washStroke(cw, ch, seed, obstacles);
  if (kind === 'glaze') return glazeStroke(cw, ch, seed);
  const first: DropKind = kind === 'auto' ? CYCLE[turn % CYCLE.length]! : kind;
  const order = [first, ...(['stroke', 'drops'] as const).filter((k) => k !== first)];
  for (const k of order) {
    if (k === 'drops') {
      const dabs = fitDrops(cw, ch, obstacles, { seed, turn, step });
      if (dabs) return { kind: 'drops', strokes: [], dabs, bounds: boundsOf([], dabs) };
    } else if (k === 'splotch') {
      const mark = fitSplotch(cw, ch, obstacles, { seed, turn, step });
      if (mark) return mark;
    } else if (k === 'border') {
      return borderMark(cw, ch, seed);
    } else {
      const st = fitStroke(cw, ch, obstacles, { seed, turn, step, spatter });
      if (st) {
        const strokes: DropMark['strokes'] = [{ path: st.path, radius: st.radius, side: st.side }];
        return { kind: 'stroke', strokes, dabs: st.spatter, bounds: boundsOf(strokes, st.spatter) };
      }
    }
  }
  return null;
}
