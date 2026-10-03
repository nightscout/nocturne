import { describe, expect, it } from 'vitest';
import { type Box, type DropStroke, MAX_DROP_RADIUS, MAX_RADIUS, MAX_THICKNESS, RESERVE_MERGE_GAP, borderMark, clearanceField, fitDrops, fitMark, fitSplotch, fitStroke, mergeReserves, washStroke } from './drop-stroke';

/** Deterministic, so a failure can be reproduced from the seed alone. */
function rng(seed: number) {
  let h = seed >>> 0;
  return () => {
    h ^= h << 13;
    h >>>= 0;
    h ^= h >>> 17;
    h ^= h << 5;
    h >>>= 0;
    return h / 0xffffffff;
  };
}

/** A feature card: icon at the left, a title and a line of copy beside it. */
const FEATURE_CARD = {
  cw: 364,
  ch: 101,
  obstacles: [
    { x: 16, y: 30, w: 40, h: 40 },
    { x: 68, y: 28, w: 154, h: 20 },
    { x: 68, y: 52, w: 232, h: 20 },
  ] as Box[],
};

/** The three surfaces the catalogue marks blobbed on. */
const BLOBBED = [
  { name: 'portal tier card', cw: 245, ch: 205, obstacles: [{ x: 20, y: 20, w: 120, h: 24 }, { x: 20, y: 56, w: 205, h: 60 }, { x: 20, y: 150, w: 90, h: 36 }] },
  { name: 'PathChoice fork', cw: 330, ch: 330, obstacles: [{ x: 40, y: 40, w: 48, h: 48 }, { x: 40, y: 110, w: 250, h: 28 }, { x: 40, y: 150, w: 250, h: 60 }] },
  { name: 'app support tile', cw: 403, ch: 142, obstacles: [{ x: 20, y: 24, w: 40, h: 40 }, { x: 76, y: 22, w: 200, h: 22 }, { x: 76, y: 50, w: 300, h: 40 }] },
] as const;

function radiusAlong(stroke: DropStroke, i: number): number {
  const [r0, r1] = stroke.radius;
  return r0 + (r1 - r0) * (i / (stroke.path.length - 1));
}

function distanceToBoxes(x: number, y: number, boxes: readonly Box[]): number {
  let best = Infinity;
  for (const b of boxes) {
    const dx = Math.max(b.x - x, 0, x - (b.x + b.w));
    const dy = Math.max(b.y - y, 0, y - (b.y + b.h));
    best = Math.min(best, Math.hypot(dx, dy));
  }
  return best;
}

function expectClear(stroke: DropStroke, obstacles: readonly Box[], step: number) {
  // The field is sampled on a grid. A point between cells can sit up to half
  // a diagonal closer to text than the cell it read.
  const slack = step * 0.75;
  stroke.path.forEach((p, i) => {
    expect(distanceToBoxes(p.x, p.y, obstacles) + slack).toBeGreaterThanOrEqual(radiusAlong(stroke, i) * 0.9);
  });
  for (const d of stroke.spatter) {
    expect(distanceToBoxes(d.x, d.y, obstacles) + slack).toBeGreaterThanOrEqual(d.r);
  }
}

describe('fitStroke', () => {
  it('is deterministic in its inputs', () => {
    const { cw, ch, obstacles } = FEATURE_CARD;
    expect(fitStroke(cw, ch, obstacles, { seed: 7 })).toEqual(fitStroke(cw, ch, obstacles, { seed: 7 }));
  });

  it('varies with the seed', () => {
    const { cw, ch, obstacles } = FEATURE_CARD;
    const paths = new Set<string>();
    for (let seed = 0; seed < 12; seed++) {
      const s = fitStroke(cw, ch, obstacles, { seed });
      expect(s).not.toBeNull();
      paths.add(JSON.stringify(s!.path.map((p) => [Math.round(p.x), Math.round(p.y)])));
    }
    expect(paths.size).toBeGreaterThan(6);
  });

  it('takes an edge on a card with text beside an icon', () => {
    const { cw, ch, obstacles } = FEATURE_CARD;
    for (let seed = 0; seed < 8; seed++) {
      expect(fitStroke(cw, ch, obstacles, { seed })!.side).not.toBe('free');
    }
  });

  it('is a stroke, not a dab, wherever it draws', () => {
    const random = rng(20260922);
    let drawn = 0;
    for (let trial = 0; trial < 400; trial++) {
      const cw = Math.round(60 + random() * 900);
      const ch = Math.round(40 + random() * 400);
      const obstacles: Box[] = [];
      for (let i = 0, n = Math.floor(random() * 8); i < n; i++) {
        obstacles.push({
          x: Math.round(random() * cw),
          y: Math.round(random() * ch),
          w: Math.round(10 + random() * cw * 0.6),
          h: Math.round(8 + random() * ch * 0.4),
        });
      }
      const s = fitStroke(cw, ch, obstacles, { seed: trial });
      if (!s) continue;
      drawn++;
      const [r0, r1] = s.radius;
      const length = s.path.reduce((acc, p, i) => (i ? acc + Math.hypot(p.x - s.path[i - 1]!.x, p.y - s.path[i - 1]!.y) : 0), 0);
      expect({ trial, ok: length >= 3 * 2 * r0 * 0.99 }).toEqual({ trial, ok: true });
      expect({ trial, ok: r1 < r0 }).toEqual({ trial, ok: true });
      expect({ trial, ok: 2 * r0 <= Math.min(cw, ch) * MAX_THICKNESS + 1e-6 }).toEqual({ trial, ok: true });
      expect({ trial, ok: r0 <= MAX_RADIUS + 1e-6 }).toEqual({ trial, ok: true });
      expect({ trial, ok: r0 * (1 + r1 / r0) * length <= 0.2 * cw * ch * 1.05 || length <= 3 * 2 * r0 * 1.01 }).toEqual({ trial, ok: true });
      // Most of the brush lands on the surface, so the centreline is on it or barely off.
      const inside = s.path.some((p, i) => Math.min(p.x, p.y, cw - p.x, ch - p.y) >= -0.1 * radiusAlong(s, i));
      expect({ trial, inside }).toEqual({ trial, inside: true });
      const step = Math.min(12, Math.max(4, Math.round(Math.min(cw, ch) / 16)));
      expectClear(s, obstacles, step);
    }
    expect(drawn).toBeGreaterThan(300);
  });

  it('draws for every seed or for none on one surface', () => {
    const random = rng(20260923);
    let mixed = 0;
    for (let trial = 0; trial < 300; trial++) {
      const cw = Math.round(60 + random() * 900);
      const ch = Math.round(40 + random() * 400);
      const obstacles: Box[] = [];
      for (let i = 0, n = Math.floor(random() * 8); i < n; i++) {
        obstacles.push({
          x: Math.round(random() * cw),
          y: Math.round(random() * ch),
          w: Math.round(10 + random() * cw * 0.6),
          h: Math.round(8 + random() * ch * 0.4),
        });
      }
      const drawn = [0, 1, 2, 3, 4, 5].map((seed) => fitStroke(cw, ch, obstacles, { seed }) !== null);
      if (drawn.some(Boolean) && !drawn.every(Boolean)) mixed++;
    }
    expect(mixed).toBe(0);
  });

  it('holds the brush to a hand-sized width on a large surface', () => {
    const s = fitStroke(330, 330, [{ x: 40, y: 40, w: 48, h: 48 }, { x: 40, y: 110, w: 250, h: 28 }], { seed: 0 })!;
    expect(s.radius[0]).toBeLessThanOrEqual(MAX_RADIUS);
  });

  it.each(BLOBBED)('stays a mark on the $name', ({ cw, ch, obstacles }) => {
    for (let seed = 0; seed < 6; seed++) {
      const s = fitStroke(cw, ch, obstacles, { seed });
      expect(s).not.toBeNull();
      // The catalogue mark here was wider than the surface; a stroke is
      // bounded by the short edge whatever the aspect.
      expect((2 * s!.radius[0]) / Math.min(cw, ch)).toBeLessThanOrEqual(MAX_THICKNESS + 1e-6);
      expectClear(s!, obstacles, Math.min(12, Math.max(4, Math.round(Math.min(cw, ch) / 16))));
    }
  });

  it('draws nothing where the content covers the surface', () => {
    expect(fitStroke(300, 200, [{ x: -60, y: -60, w: 420, h: 320 }])).toBeNull();
  });

  it('draws on an empty surface', () => {
    const s = fitStroke(300, 200, []);
    expect(s).not.toBeNull();
    expect(s!.spatter.length).toBeGreaterThan(0);
  });

  it('can be asked for no spatter', () => {
    expect(fitStroke(300, 200, [], { spatter: false })!.spatter).toEqual([]);
  });

  it('keeps the bounds around every point of paint', () => {
    const s = fitStroke(300, 200, [], { seed: 3 })!;
    const b = s.bounds;
    s.path.forEach((p, i) => {
      const r = radiusAlong(s, i);
      expect(p.x - r).toBeGreaterThanOrEqual(b.x - 1e-6);
      expect(p.y - r).toBeGreaterThanOrEqual(b.y - 1e-6);
      expect(p.x + r).toBeLessThanOrEqual(b.x + b.w + 1e-6);
      expect(p.y + r).toBeLessThanOrEqual(b.y + b.h + 1e-6);
    });
  });
});

describe('clearanceField', () => {
  it('reads zero inside an obstacle and the gap beside it', () => {
    const f = clearanceField(100, 100, [{ x: 40, y: 40, w: 20, h: 20 }], { step: 4, margin: 8 });
    expect(f.at(50, 50)).toBe(0);
    expect(f.at(50, 20)).toBeCloseTo(20, 0);
  });

  it('clamps a query outside the grid to its edge', () => {
    const f = clearanceField(100, 100, [], { step: 4, margin: 8 });
    expect(f.at(-500, -500)).toBe(f.at(-8, -8));
  });
});

describe('fitDrops', () => {
  it('places two or three drops clear of the text and of each other', () => {
    const random = rng(20260924);
    let drawn = 0;
    for (let trial = 0; trial < 200; trial++) {
      const cw = Math.round(120 + random() * 700);
      const ch = Math.round(80 + random() * 300);
      const obstacles: Box[] = [];
      for (let i = 0, n = Math.floor(random() * 6); i < n; i++) {
        obstacles.push({ x: Math.round(random() * cw), y: Math.round(random() * ch), w: Math.round(10 + random() * cw * 0.5), h: Math.round(8 + random() * ch * 0.3) });
      }
      const dabs = fitDrops(cw, ch, obstacles, { seed: trial, turn: trial });
      if (!dabs) continue;
      drawn++;
      expect({ trial, n: dabs.length >= 2 && dabs.length <= 3 }).toEqual({ trial, n: true });
      const step = Math.min(12, Math.max(4, Math.round(Math.min(cw, ch) / 16)));
      for (const d of dabs) {
        expect({ trial, ok: d.r <= MAX_DROP_RADIUS + 1e-6 && d.wet }).toEqual({ trial, ok: true });
        expect(distanceToBoxes(d.x, d.y, obstacles) + step * 0.75).toBeGreaterThanOrEqual(d.r * 0.9);
      }
      for (let i = 1; i < dabs.length; i++) {
        expect({ trial, smaller: dabs[i]!.r < dabs[0]!.r }).toEqual({ trial, smaller: true });
        for (let j = 0; j < i; j++) {
          expect(Math.hypot(dabs[i]!.x - dabs[j]!.x, dabs[i]!.y - dabs[j]!.y)).toBeGreaterThan(dabs[i]!.r + dabs[j]!.r);
        }
      }
    }
    expect(drawn).toBeGreaterThan(120);
  });

  it('is deterministic and varies with the seed', () => {
    const a = fitDrops(300, 200, [], { seed: 4 });
    expect(a).toEqual(fitDrops(300, 200, [], { seed: 4 }));
    expect(a).not.toEqual(fitDrops(300, 200, [], { seed: 5 }));
  });
});

describe('washStroke', () => {
  it('covers the whole control and runs off both ends', () => {
    const w = washStroke(160, 40, 7);
    const [st] = w.strokes;
    expect(w.kind).toBe('wash');
    expect(st!.radius[0]).toBeGreaterThanOrEqual(40 * 0.6);
    expect(st!.path[0]!.x).toBeLessThan(0);
    expect(st!.path.at(-1)!.x).toBeGreaterThan(160);
    expect(w.bounds.y).toBeLessThan(0);
    expect(w.bounds.y + w.bounds.h).toBeGreaterThan(40);
  });
});

describe('fitMark', () => {
  const { cw, ch, obstacles } = FEATURE_CARD;

  it('cycles the gestures along a run', () => {
    const kinds = [0, 1, 2, 3, 4].map((turn) => fitMark(cw, ch, obstacles, { seed: turn, turn })!.kind);
    expect(kinds).toEqual(['stroke', 'drops', 'splotch', 'border', 'stroke']);
  });

  it('hands over to the other gesture when the asked one does not fit', () => {
    // A hairline row has no room for two drops.
    const row = fitMark(420, 40, [{ x: 8, y: 8, w: 380, h: 24 }], { kind: 'drops', seed: 1 });
    expect(row === null || row.kind === 'stroke').toBe(true);
  });

  it('honours an explicit kind', () => {
    expect(fitMark(cw, ch, obstacles, { kind: 'stroke' })!.kind).toBe('stroke');
    expect(fitMark(cw, ch, obstacles, { kind: 'wash' })!.kind).toBe('wash');
  });
});

describe('mergeReserves', () => {
  it('joins a block of lines and its glyph, chains included, into one box', () => {
    const glyph = { x: 16, y: 16, w: 40, h: 40 };
    const lines = [0, 1, 2].map((i) => ({ x: 68, y: 12 + i * 20, w: 150, h: 20 }));
    const [block, ...rest] = mergeReserves([glyph, ...lines]);
    expect(rest).toHaveLength(0);
    expect(block).toEqual({ x: 16, y: 12, w: 202, h: 60 });
  });

  it('keeps boxes further apart than the gap separate', () => {
    const a = { x: 0, y: 40, w: 50, h: 20 };
    const b = { x: 50 + RESERVE_MERGE_GAP + 1, y: 40, w: 50, h: 20 };
    expect(mergeReserves([a, b])).toHaveLength(2);
  });

  it('runs a block near the surface edge out past it, and leaves a far one alone', () => {
    const near = { x: 8, y: 30, w: 100, h: 40 };
    const [block] = mergeReserves([near], { cw: 364, ch: 101, bleed: 18 });
    expect(block!.x).toBe(-18);
    expect(block!.y).toBe(30);
    expect(block!.x + block!.w).toBe(108);
    const far = { x: 120, y: 30, w: 100, h: 40 };
    expect(mergeReserves([far], { cw: 364, ch: 101, bleed: 18 })).toEqual([far]);
  });

  it('is what a wash reserves', () => {
    const w = washStroke(364, 101, 1, [{ x: 16, y: 16, w: 40, h: 40 }, { x: 68, y: 12, w: 150, h: 20 }]);
    expect(w.reserves).toHaveLength(1);
  });
});

describe('borderMark', () => {
  it('runs round the perimeter, mostly on the surface, and stops short of closing', () => {
    const m = borderMark(364, 101, 3);
    const [st] = m.strokes;
    expect(m.kind).toBe('border');
    for (const p of st!.path) {
      const toEdge = Math.min(Math.abs(p.x), Math.abs(p.y), Math.abs(364 - p.x), Math.abs(101 - p.y));
      expect(toEdge).toBeLessThanOrEqual(st!.radius[0] * 0.2 + 14 + 2);
    }
    // Overlaps the edge: the brush reaches past the card on every side.
    expect(m.bounds.x).toBeLessThan(0);
    expect(m.bounds.x + m.bounds.w).toBeGreaterThan(364);
    expect(st!.radius[1]).toBeLessThan(st!.radius[0]);
    const a = st!.path[0]!;
    const b = st!.path.at(-1)!;
    expect(Math.hypot(a.x - b.x, a.y - b.y)).toBeGreaterThan(20);
    expect(m.bounds.w).toBeGreaterThan(364 - 2 * st!.radius[0]);
    expect(m.bounds.h).toBeGreaterThan(101 - 2 * st!.radius[0]);
  });
});

describe('fitSplotch', () => {
  const card = { cw: 364, ch: 101, obstacles: [{ x: 16, y: 30, w: 40, h: 40 }, { x: 68, y: 28, w: 232, h: 44 }] };

  it('is one circle taller than the surface, filling the band beside the copy', () => {
    const m = fitSplotch(card.cw, card.ch, card.obstacles, { seed: 1 })!;
    expect(m.kind).toBe('splotch');
    expect(m.strokes).toHaveLength(0);
    expect(m.dabs).toHaveLength(1);
    const d = m.dabs[0]!;
    expect(d.wet).toBe(true);
    expect(d.r).toBeGreaterThan(card.ch / 2);
    // Its near edge sits in the clear band, past the copy.
    expect(d.x - d.r).toBeGreaterThanOrEqual(300);
    expect(d.x - d.r).toBeLessThan(card.cw);
    expect(m.bounds.y).toBeLessThan(0);
    expect(m.bounds.y + m.bounds.h).toBeGreaterThan(card.ch);
  });

  it('takes the left band when the copy is on the right', () => {
    const m = fitSplotch(364, 101, [{ x: 120, y: 20, w: 236, h: 60 }], { seed: 1 })!;
    const d = m.dabs[0]!;
    expect(d.x + d.r).toBeLessThanOrEqual(120);
    expect(d.x + d.r).toBeGreaterThan(0);
  });

  it('gives up when the copy leaves no band, and when nothing is in the way', () => {
    expect(fitSplotch(300, 100, [{ x: 8, y: 8, w: 284, h: 84 }])).toBeNull();
    expect(fitSplotch(300, 100, [])).toBeNull();
  });
});
