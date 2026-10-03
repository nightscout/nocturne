import { describe, expect, it } from 'vitest';
import appendedOperations from '../../../../../../crates/nocturne-watercolour-infra/tests/fixtures/appended-operations.json';
import type { TimedOp } from '../types';
import { PackingSuitcase, packingSuitcaseScene } from './packing-suitcase';

const OUTLINE: [number, number][] = [
  [0.12, 0.34],
  [0.88, 0.34],
  [0.88, 0.84],
  [0.12, 0.84],
];
const BODY_BOTTOM = 0.84;
const PIGMENTS = { bands: [0, 2, 3], hardware: 1, clasp: 4 };

type Brush = { path: [number, number][]; radius: [number, number]; pigment: number; concentration: number };
type Lift = { path: [number, number][]; strength: number };

const opsOf = <T>(ops: TimedOp[], kind: string) =>
  ops.flatMap(({ op }) => (typeof op === 'object' && kind in op ? [op[kind] as T] : []));
const brushes = (ops: TimedOp[]) => opsOf<Brush>(ops, 'brush');
const lifts = (ops: TimedOp[]) => opsOf<Lift>(ops, 'lift');
const rowsOf = (ops: TimedOp[]) => brushes(ops).filter((b) => b.pigment !== PIGMENTS.hardware && b.pigment !== PIGMENTS.clasp);
const lowestRow = (ops: TimedOp[]) => Math.max(...rowsOf(ops).flatMap((b) => b.path.map((p) => p[1])));
const highestRow = (ops: TimedOp[]) => Math.min(...rowsOf(ops).flatMap((b) => b.path.map((p) => p[1])));
const rowRadius = (ops: TimedOp[]) => rowsOf(ops)[0]!.radius[0];
const hasHardware = (ops: TimedOp[]) => brushes(ops).some((b) => b.pigment === PIGMENTS.hardware);

const suitcase = () => new PackingSuitcase({ outline: OUTLINE, pigments: PIGMENTS });

/** Packs `n` items in a list of `n`, returning each pack's operations. */
function packAll(painter: PackingSuitcase, n: number) {
  return Array.from({ length: n }, (_, i) => painter.pack(`category ${i}`, n - 1 - i));
}

describe('PackingSuitcase', () => {
  it.each([2, 10, 40])('fills the body to its bottom exactly when the last of %i items is packed', (n) => {
    const packs = packAll(suitcase(), n);
    const last = packs.at(-1)!;
    expect(BODY_BOTTOM - lowestRow(last)).toBeLessThanOrEqual(rowRadius(last));
    for (const pack of packs.slice(0, -1)) expect(BODY_BOTTOM - lowestRow(pack)).toBeGreaterThan(rowRadius(pack));
  });

  it('paints each band below the last, reaching back over it', () => {
    const packs = packAll(suitcase(), 6);
    for (let i = 1; i < packs.length; i++) {
      expect(lowestRow(packs[i]!)).toBeGreaterThan(lowestRow(packs[i - 1]!));
      expect(highestRow(packs[i]!) - rowRadius(packs[i]!)).toBeLessThan(lowestRow(packs[i - 1]!) + rowRadius(packs[i - 1]!));
    }
  });

  it('gives fewer items bolder bands', () => {
    const few = suitcase().pack('clothes', 1);
    const many = suitcase().pack('clothes', 39);
    expect(rowRadius(few)).toBeGreaterThan(rowRadius(many) * 5);
  });

  it('thins the bands still to come when an item is added, leaving painted ones alone', () => {
    const steady = suitcase();
    const grown = suitcase();
    const first = steady.pack('clothes', 3);
    expect(grown.pack('clothes', 3)).toEqual(first);
    expect(rowRadius(grown.pack('toiletries', 5))).toBeLessThan(rowRadius(steady.pack('toiletries', 2)));
  });

  it('keeps a category on one pigment and draws bands only from the band pigments', () => {
    const a = rowsOf(suitcase().pack('Toiletries', 3))[0]!.pigment;
    expect(rowsOf(suitcase().pack('Toiletries', 9))[0]!.pigment).toBe(a);
    const seen = new Set(['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'].map((c) => rowsOf(suitcase().pack(c, 1))[0]!.pigment));
    for (const p of seen) expect(PIGMENTS.bands).toContain(p);
  });

  it('adds the hardware only when the list completes', () => {
    const packs = packAll(suitcase(), 4);
    expect(packs.slice(0, -1).some(hasHardware)).toBe(false);
    expect(hasHardware(packs.at(-1)!)).toBe(true);
    expect(brushes(packs.at(-1)!).some((b) => b.pigment === PIGMENTS.clasp && b.path.length === 1)).toBe(true);
  });

  it('glazes the whole body for an item packed after it is full, without new hardware', () => {
    const painter = suitcase();
    packAll(painter, 3);
    const extra = painter.pack('late addition', 0);
    expect(hasHardware(extra)).toBe(false);
    expect(Math.max(...rowsOf(extra).map((b) => b.concentration))).toBeLessThan(0.2);
  });

  it('lifts the lowest band on an unpack, so the next pack repaints that slot', () => {
    const painter = suitcase();
    const packs = [painter.pack('a', 4), painter.pack('b', 3)];
    const lift = lifts(painter.unpack());
    expect(lift).toHaveLength(1);
    expect(lift[0]!.path).toEqual(rowsOf(packs[1]!)[0]!.path);
    expect(lift[0]!.strength).toBeLessThan(1);
    expect(rowsOf(painter.pack('c', 3))[0]!.path).toEqual(rowsOf(packs[1]!)[0]!.path);
  });

  it('takes the hardware off a complete suitcase before its lowest band, and puts it back on repacking', () => {
    const painter = suitcase();
    const packs = packAll(painter, 3);
    const unpack = painter.unpack();
    const lifted = lifts(unpack).map((l) => l.path);
    expect(lifted).toHaveLength(3);
    expect(lifted.at(-1)).toEqual(rowsOf(packs.at(-1)!)[0]!.path);
    expect(hasHardware(painter.pack('again', 0))).toBe(true);
  });

  it('runs the lowest band to the bottom and adds the hardware when removing an item completes the list', () => {
    const painter = suitcase();
    const packs = [painter.pack('a', 2), painter.pack('b', 1)];
    expect(painter.countChanged(1)).toEqual([]);
    const done = painter.countChanged(0);
    expect(hasHardware(done)).toBe(true);
    expect(BODY_BOTTOM - lowestRow(done)).toBeLessThanOrEqual(rowRadius(done));
    expect(rowsOf(done)[0]!.pigment).toBe(rowsOf(packs[1]!)[0]!.pigment);
    expect(painter.countChanged(0)).toEqual([]);
    const lifted = lifts(painter.unpack()).at(-1)!;
    expect(Math.max(...lifted.path.map((p) => p[1]))).toBeGreaterThan(lowestRow(packs[1]!));
  });

  it('sends only the operation forms the engine reads', () => {
    const fixture = appendedOperations as { op: unknown }[];
    const shape = (op: unknown): string =>
      typeof op === 'string' ? op : Object.entries(op as Record<string, unknown>).map(([kind, body]) => `${kind}:${Object.keys(body as object).sort().join(',')}`).join();
    const forms = new Set(fixture.map(({ op }) => shape(op)));
    const painter = suitcase();
    const ops = [...packAll(painter, 3).flat(), ...painter.pack('late', 0), ...painter.unpack(), ...painter.unpack(), ...painter.countChanged(0)];
    const sent = new Set(ops.map(({ op }) => shape(op)));
    for (const form of sent) expect(forms).toContain(form);
  });

  it('does nothing for an unpack with nothing painted', () => {
    expect(suitcase().unpack()).toEqual([]);
  });

  it('keeps every stroke on the canvas', () => {
    const painter = suitcase();
    const ops = [...packAll(painter, 12).flat(), ...painter.unpack(), ...painter.pack('x', 0)];
    const points = [...brushes(ops), ...lifts(ops), ...opsOf<{ path: [number, number][] }>(ops, 'water')].flatMap((s) => s.path);
    for (const [x, y] of points) {
      expect(x).toBeGreaterThanOrEqual(0);
      expect(x).toBeLessThanOrEqual(1);
      expect(y).toBeGreaterThanOrEqual(0);
      expect(y).toBeLessThanOrEqual(1);
    }
  });

  it('restores the silhouette before painting, since the handle is painted without it', () => {
    const painter = suitcase();
    for (const pack of packAll(painter, 3)) {
      const firstPaint = pack.findIndex(({ op }) => typeof op === 'object' && 'brush' in op);
      expect(pack.slice(0, firstPaint).some(({ op }) => typeof op === 'object' && 'set_mask' in op)).toBe(true);
    }
  });
});

describe('packingSuitcaseScene', () => {
  const donor = JSON.stringify({
    version: 1,
    id: 'suitcase-dusk-0',
    palette: {
      name: 'dusk',
      entries: [
        { role: 'base_wash', pigment: {} },
        { role: 'shadow', pigment: {} },
        { role: 'accent', pigment: {} },
        { role: 'glow', pigment: {} },
      ],
    },
    timeline: {
      total_ticks: 340,
      events: [{ at_tick: 0, op: { set_mask: { mask: { polygon: { points: OUTLINE, feather: 0.01 } } } } }],
    },
  });
  const calls: unknown[][] = [];
  const module = {
    catalogueScene(...args: unknown[]) {
      calls.push(args);
      return donor;
    },
  };
  type Doc = { timeline: { total_ticks: number; events: { at_tick: number; op: unknown }[] } };

  it('borrows the catalogue suitcase in dusk', () => {
    packingSuitcaseScene(module, 120, 120, { packed: ['clothes'], unpacked: 2 });
    expect(calls.at(-1)?.slice(0, 3)).toEqual(['suitcase', 0, 'dusk']);
  });

  it('sizes the grid to the canvas, up to the grid the strokes were tuned on', () => {
    packingSuitcaseScene(module, 64, 64, { packed: [], unpacked: 2, dpr: 2 });
    const small = calls.at(-1)![6] as number;
    packingSuitcaseScene(module, 192, 192, { packed: [], unpacked: 2, dpr: 2 });
    const large = calls.at(-1)![6] as number;
    expect(small).toBeGreaterThanOrEqual(128);
    expect(large).toBeGreaterThan(small);
    expect(large).toBeLessThanOrEqual(320);
  });

  it('replays the packed items in order, dried in turn, ending wet-ready for the live session', () => {
    const { sceneJson } = packingSuitcaseScene(module, 120, 120, { packed: ['a', 'b', 'c'], unpacked: 2 });
    const { timeline } = JSON.parse(sceneJson) as Doc;
    const ticks = timeline.events.map((e) => e.at_tick);
    expect(ticks).toEqual([...ticks].sort((x, y) => x - y));
    expect(timeline.events.filter((e) => e.op === 'dry_all')).toHaveLength(3);
    const last = timeline.events.at(-1)!.op as { settle?: { share: number } };
    expect(last.settle?.share).toBeGreaterThan(0);
    expect(Math.max(...ticks)).toBeLessThan(timeline.total_ticks);
  });

  it('gives the last replayed band time to spread before the sheet dries for the hardware', () => {
    const { sceneJson } = packingSuitcaseScene(module, 120, 120, { packed: ['a', 'b'], unpacked: 0 });
    const { timeline } = JSON.parse(sceneJson) as Doc;
    const isBand = (op: unknown) => typeof op === 'object' && op !== null && 'brush' in op && (op as { brush: Brush }).brush.pigment !== 1;
    const lastBandTick = Math.max(...timeline.events.filter((e) => isBand(e.op)).map((e) => e.at_tick));
    const dried = timeline.events.find((e) => e.op === 'dry_all' && e.at_tick >= lastBandTick)!;
    expect(dried.at_tick - lastBandTick).toBeGreaterThanOrEqual(5);
  });

  it('refuses a donor without the pigments it paints with', () => {
    const noShadow = { catalogueScene: () => donor.replace('"shadow"', '"other"') };
    expect(() => packingSuitcaseScene(noShadow, 120, 120, { packed: [], unpacked: 1 })).toThrow(/shadow/);
  });

  it('lays a completed list’s hardware without the live drying waits', () => {
    const { sceneJson } = packingSuitcaseScene(module, 120, 120, { packed: ['a', 'b'], unpacked: 0 });
    const { timeline } = JSON.parse(sceneJson) as Doc;
    expect(timeline.total_ticks).toBeLessThan(120);
  });

  it('hands over a painter that continues from the replayed bands', () => {
    const replayed = packingSuitcaseScene(module, 120, 120, { packed: ['a', 'b'], unpacked: 1 }).painter;
    const fresh = new PackingSuitcase({ outline: OUTLINE, pigments: { bands: [0, 2, 3], hardware: 1, clasp: 3 } });
    fresh.pack('a', 2);
    fresh.pack('b', 1);
    expect(replayed.pack('c', 0)).toEqual(fresh.pack('c', 0));
  });
});
