import { describe, expect, it } from 'vitest';
import { brushworkMs, dropScene, strokeFrame } from './drop-scene';
import { type DropMark, bleedFor, fitMark, glazeStroke, washStroke } from './drop-stroke';

/** A donor the shape of a real catalogue document, with a recognisable palette order. */
const donor = (surface: string) =>
  JSON.stringify({
    version: 1,
    id: 'avatar-wash-water-0',
    size_hint: [512, 512],
    seed: 0,
    sim_resolution: 256,
    background: surface === 'dark' ? 'transparent_on_dark' : 'transparent',
    paper: { seed: 0, grain_scale: 140, height_amplitude: 0.08, absorbency: [0.4, 0.9], fibre_anisotropy: 0.35 },
    palette: {
      name: 'water',
      entries: [
        { role: 'shadow', pigment: { name: 'indigo' } },
        { role: 'base_wash', pigment: { name: 'cerulean' } },
        { role: 'accent', pigment: { name: 'rose' } },
      ],
    },
    timeline: { total_ticks: 320, events: [{ at_tick: 0, op: { dry_all: null } }] },
  });

const calls: unknown[][] = [];
const module = {
  catalogueScene(...args: unknown[]) {
    calls.push(args);
    return donor(String(args[5]));
  },
};

const CARD = { cw: 364, ch: 101, obstacles: [{ x: 16, y: 30, w: 40, h: 40 }, { x: 68, y: 28, w: 232, h: 44 }] };
const stroke = fitMark(CARD.cw, CARD.ch, CARD.obstacles, { seed: 3, kind: 'stroke' })!;
const drops = fitMark(300, 200, [{ x: 20, y: 80, w: 200, h: 40 }], { seed: 1, kind: 'drops' })!;
const wash = fitMark(160, 40, [], { seed: 2, kind: 'wash' })!;

type Doc = {
  size_hint: [number, number];
  sim_resolution: number;
  background: string;
  palette: { entries: { role: string }[] };
  timeline: { total_ticks: number; events: { at_tick: number; op: Record<string, Record<string, unknown>> | string }[] };
};
const parse = (json: string) => JSON.parse(json) as Doc;

describe('strokeFrame', () => {
  it('holds the paint with room to bloom and stays inside the bleed', () => {
    const f = strokeFrame(stroke, CARD.cw, CARD.ch);
    const b = stroke.bounds;
    const m = bleedFor(CARD.cw, CARD.ch);
    // Paint past the bleed is never seen, so the frame holds the bounds only
    // as far as the bleed reaches.
    expect(f.x).toBeLessThanOrEqual(Math.max(b.x, -m));
    expect(f.y).toBeLessThanOrEqual(Math.max(b.y, -m));
    expect(f.x + f.w).toBeGreaterThanOrEqual(Math.min(b.x + b.w, CARD.cw + m));
    expect(f.y + f.h).toBeGreaterThanOrEqual(Math.min(b.y + b.h, CARD.ch + m));
    expect(f.x).toBeGreaterThanOrEqual(-m);
    expect(f.y).toBeGreaterThanOrEqual(-m);
    expect(f.x + f.w).toBeLessThanOrEqual(CARD.cw + m);
    expect(f.y + f.h).toBeLessThanOrEqual(CARD.ch + m);
  });

  it('pads the short side of a very elongated stroke', () => {
    const hairline: DropMark = {
      kind: 'stroke',
      strokes: [{ path: [{ x: 0, y: 50 }, { x: 900, y: 50 }], radius: [6, 3], side: 'free' }],
      dabs: [],
      bounds: { x: 0, y: 44, w: 900, h: 12 },
    };
    const f = strokeFrame(hairline, 900, 400);
    // Padded to the aspect limit, or as far as the bleed allows when that comes first.
    const m = bleedFor(900, 400);
    expect(f.w / f.h <= 3 + 1e-6 || f.h >= 400 + 2 * m - 1e-6).toBe(true);
    expect(f.h).toBeGreaterThan(12 * 4);
  });
});

describe('dropScene', () => {
  it('borrows the donor and keeps every coordinate on the canvas', () => {
    calls.length = 0;
    const { sceneJson, frame } = dropScene(module, stroke, CARD.cw, CARD.ch, { palette: 'ember', surface: 'dark', seed: 9, dpr: 2 });
    expect(calls[0]![0]).toBe('avatar-wash');
    expect(calls[0]![2]).toBe('ember');
    expect(calls[0]![5]).toBe('dark');
    const doc = parse(sceneJson);
    expect(doc.background).toBe('transparent_on_dark');
    expect(doc.size_hint).toEqual([Math.round(frame.w * 2), Math.round(frame.h * 2)]);
    expect(doc.sim_resolution).toBeLessThanOrEqual(512);
    expect(doc.sim_resolution).toBeGreaterThanOrEqual(Math.min(512, Math.max(frame.w, frame.h) * 2 - 32));
    for (const e of doc.timeline.events) {
      if (typeof e.op === 'string') continue;
      const op = Object.values(e.op)[0]!;
      const path = op.path as [number, number][] | undefined;
      if (!path) continue;
      for (const [x, y] of path) {
        expect(x).toBeGreaterThanOrEqual(0);
        expect(x).toBeLessThanOrEqual(1);
        expect(y).toBeGreaterThanOrEqual(0);
        expect(y).toBeLessThanOrEqual(1);
      }
      const [ra, rb] = op.radius as [number, number];
      expect(ra).toBeGreaterThan(0);
      expect(rb).toBeGreaterThan(0);
    }
  });

  it('keeps every coordinate on the canvas for every gesture, size and seed', () => {
    // The engine rejects a scene with any point off the canvas, and a rejected
    // scene draws nothing at all.
    const surfaces: [number, number, { x: number; y: number; w: number; h: number }[]][] = [
      [330, 330, [{ x: 24, y: 24, w: 48, h: 48 }, { x: 24, y: 88, w: 240, h: 20 }, { x: 24, y: 124, w: 170, h: 64 }]],
      [245, 205, [{ x: 20, y: 20, w: 90, h: 20 }, { x: 20, y: 52, w: 170, h: 40 }, { x: 20, y: 160, w: 70, h: 32 }]],
      [403, 142, [{ x: 176, y: 16, w: 50, h: 50 }, { x: 110, y: 76, w: 180, h: 44 }]],
      [CARD.cw, CARD.ch, CARD.obstacles],
      [939, 61, [{ x: 16, y: 12, w: 240, h: 36 }]],
      [160, 40, [{ x: 24, y: 10, w: 110, h: 20 }]],
    ];
    const kinds = ['stroke', 'drops', 'splotch', 'border', 'wash', 'glaze'] as const;
    const off: string[] = [];
    for (const [cw, ch, obstacles] of surfaces) {
      for (const kind of kinds) {
        for (let seed = 0; seed < 24; seed++) {
          const mark = fitMark(cw, ch, obstacles, { kind, seed, turn: seed });
          if (!mark) continue;
          const doc = parse(dropScene(module, mark, cw, ch, { seed }).sceneJson);
          doc.timeline.events.forEach((e, i) => {
            if (typeof e.op === 'string') return;
            const op = Object.values(e.op)[0]!;
            const points = [...((op.path as [number, number][] | undefined) ?? []), ...(op.center ? [op.center as [number, number]] : [])];
            for (const [x, y] of points) {
              if (x < 0 || x > 1 || y < 0 || y > 1) off.push(`${cw}x${ch} ${mark.kind} seed ${seed} event ${i}: ${x.toFixed(3)},${y.toFixed(3)}`);
            }
          });
        }
      }
    }
    expect(off).toEqual([]);
  });

  it('draws a stroke along its path, one span a tick, then spends the rest settling', () => {
    const doc = parse(dropScene(module, stroke, CARD.cw, CARD.ch, { deposit: 'stamp' }).sceneJson);
    // The stroke's spans; the spatter is flicked, one whole laydown per droplet.
    const brushes = doc.timeline.events.filter(
      (e) => typeof e.op !== 'string' && 'brush' in e.op && (e.op as Record<string, Record<string, unknown>>).brush!.span,
    );
    const spans = brushes.map((e) => (e.op as Record<string, Record<string, unknown>>).brush!.span as [number, number]);
    expect(brushes.length).toBeGreaterThan(1);
    expect(spans[0]![0]).toBe(0);
    expect(spans.at(-1)![1]).toBe(1);
    spans.slice(1).forEach((s, i) => expect(s[0]).toBeCloseTo(spans[i]![1]));
    brushes.slice(1).forEach((e, i) => expect(e.at_tick).toBe(brushes[i]!.at_tick + 1));
    expect(doc.timeline.events.at(-1)).toEqual({ at_tick: doc.timeline.total_ticks, op: 'dry_all' });
    expect(doc.timeline.events.some((e) => typeof e.op !== 'string' && 'dry' in e.op)).toBe(true);
  });

  it('lays a drop down whole, not drawn', () => {
    const doc = parse(dropScene(module, drops, 300, 200, { deposit: 'wet' }).sceneJson);
    const brushes = doc.timeline.events.filter((e) => typeof e.op !== 'string' && 'brush' in e.op);
    expect(brushes.every((e) => (e.op as Record<string, Record<string, unknown>>).brush!.span === undefined)).toBe(true);
  });

  it('gives a long drawn stroke more of the clock than a short one, and a laydown none', () => {
    const row = fitMark(939, 61, [{ x: 16, y: 12, w: 240, h: 36 }], { kind: 'stroke', seed: 1 })!;
    expect(brushworkMs(row)).toBeGreaterThan(brushworkMs(stroke));
    expect(brushworkMs(drops)).toBe(0);
  });

  it('resolves pigments by role, not by position', () => {
    const doc = parse(dropScene(module, stroke, CARD.cw, CARD.ch, { deposit: 'wet' }).sceneJson);
    const role = (b: Record<string, unknown>) => doc.palette.entries[b.pigment as number]!.role;
    const brushes = doc.timeline.events.flatMap((e) => (typeof e.op !== 'string' && e.op.brush ? [e.op.brush] : []));
    // The head is the one laid at a single point; the rest are the stroke's spans.
    const head = brushes.find((b) => (b.path as unknown[]).length === 1)!;
    expect(role(brushes[0]!)).toBe('base_wash');
    expect(role(head)).toBe('shadow');
  });

  it('carries only the pigments the drop lays down', () => {
    const roles = (m: DropMark, deposit: 'wet' | 'stamp', cw: number, ch: number) => {
      const doc = parse(dropScene(module, m, cw, ch, { deposit }).sceneJson);
      for (const e of doc.timeline.events) {
        if (typeof e.op === 'string' || !e.op.brush) continue;
        const at = e.op.brush.pigment as number;
        expect(at).toBeGreaterThanOrEqual(0);
        expect(at).toBeLessThan(doc.palette.entries.length);
      }
      return doc.palette.entries.map((e) => e.role);
    };
    expect(roles(stroke, 'wet', CARD.cw, CARD.ch)).toEqual(['shadow', 'base_wash']);
    expect(roles(stroke, 'stamp', CARD.cw, CARD.ch)).toEqual(['base_wash']);
    expect(roles(drops, 'wet', 300, 200)).toEqual(['shadow', 'base_wash']);
  });

  it('wets the paper before the pigment when asked', () => {
    const wet = parse(dropScene(module, stroke, CARD.cw, CARD.ch, { deposit: 'wet' }).sceneJson);
    const stamp = parse(dropScene(module, stroke, CARD.cw, CARD.ch, { deposit: 'stamp' }).sceneJson);
    const first = wet.timeline.events[0]!.op;
    expect(typeof first !== 'string' && 'water' in first).toBe(true);
    expect(stamp.timeline.events.some((e) => typeof e.op !== 'string' && 'water' in e.op)).toBe(false);
  });

  it('paints a dab per droplet', () => {
    const doc = parse(dropScene(module, stroke, CARD.cw, CARD.ch, { deposit: 'stamp' }).sceneJson);
    const dabs = doc.timeline.events.filter((e) => {
      if (typeof e.op === 'string' || !e.op.brush) return false;
      return (e.op.brush.path as unknown[]).length === 1;
    });
    expect(dabs).toHaveLength(stroke.dabs.length);
  });

  it('lays every drop wet, water first, and never a stroke', () => {
    expect(drops.kind).toBe('drops');
    expect(drops.strokes).toHaveLength(0);
    expect(drops.dabs.length).toBeGreaterThanOrEqual(2);
    const doc = parse(dropScene(module, drops, 300, 200, { deposit: 'wet' }).sceneJson);
    const waters = doc.timeline.events.filter((e) => typeof e.op !== 'string' && 'water' in e.op);
    expect(waters).toHaveLength(drops.dabs.length);
  });

  it('lands the drops one after another, largest first', () => {
    const doc = parse(dropScene(module, drops, 300, 200, { deposit: 'wet' }).sceneJson);
    const waters = doc.timeline.events.filter((e) => typeof e.op !== 'string' && 'water' in e.op);
    const ticks = waters.map((e) => e.at_tick);
    const radii = waters.map((e) => ((e.op as Record<string, Record<string, unknown>>).water!.radius as number[])[0]!);
    expect(ticks[0]).toBe(0);
    const gaps = ticks.slice(1).map((t, i) => t - ticks[i]!);
    expect(gaps.every((g) => g === gaps[0] && g > 0)).toBe(true);
    for (let i = 1; i < radii.length; i++) expect(radii[i]!).toBeLessThanOrEqual(radii[i - 1]!);
    // Slight: the last drop lands well inside the brushwork's share of the clock.
    expect(ticks.at(-1)!).toBeLessThanOrEqual(12);
  });

  it('lifts a wash back out of its reserves, row by row, once it lands', () => {
    const block = { x: 16, y: 12, w: 202, h: 60 };
    const mark = { ...washStroke(364, 101, 1), reserves: [block] };
    const doc = parse(dropScene(module, mark, 364, 101).sceneJson);
    const lifts = doc.timeline.events.filter((e) => typeof e.op !== 'string' && 'lift' in e.op);
    const brush = doc.timeline.events.find((e) => typeof e.op !== 'string' && 'brush' in e.op)!;
    expect(lifts.length).toBeGreaterThan(1);
    expect(lifts.every((e) => e.at_tick > brush.at_tick)).toBe(true);
    // The stacked rows reach the block's top and bottom edges.
    const frame = strokeFrame(mark, 364, 101);
    const ys = lifts.map((e) => ((e.op as Record<string, Record<string, unknown>>).lift!.path as number[][])[0]![1]! * frame.h + frame.y);
    const r = 10;
    expect(Math.min(...ys) - r).toBeLessThanOrEqual(block.y + 1e-6);
    expect(Math.max(...ys) + r).toBeGreaterThanOrEqual(block.y + block.h - 1e-6);
  });

  it('lays a glaze lighter than a wash reserved round its copy', () => {
    const conc = (m: DropMark) => {
      const doc = parse(dropScene(module, m, 160, 40, { deposit: 'stamp' }).sceneJson);
      const e = doc.timeline.events.find((ev) => typeof ev.op !== 'string' && 'brush' in ev.op)!;
      return (e.op as Record<string, Record<string, unknown>>).brush!.concentration as number;
    };
    const glaze = glazeStroke(160, 40, 2);
    const reserved = washStroke(160, 40, 2, [{ x: 40, y: 12, w: 80, h: 16 }]);
    expect(glaze.kind).toBe('glaze');
    expect(glaze.reserves ?? []).toHaveLength(0);
    expect(conc(glaze)).toBeLessThan(conc(reserved));
  });

  it('lays a wash lighter than a mark beside a label', () => {
    const conc = (m: DropMark) => {
      const doc = parse(dropScene(module, m, 160, 40, { deposit: 'stamp' }).sceneJson);
      const brush = doc.timeline.events.find((e) => typeof e.op !== 'string' && 'brush' in e.op)!.op as Record<string, Record<string, number>>;
      return brush.brush!.concentration!;
    };
    const plain = fitMark(160, 40, [], { seed: 2, kind: 'stroke' })!;
    expect(conc(wash)).toBeLessThan(conc(plain));
  });
});
