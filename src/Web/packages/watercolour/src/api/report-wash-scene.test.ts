import { describe, expect, it } from 'vitest';
import { MAX_FRAME_ASPECT } from './drop-scene';
import { reportWashCrop, reportWashScene } from './report-wash-scene';
import { parseSceneDocument } from './scenes';

const module = {
  catalogueScene: () => JSON.stringify({
    version: 1,
    paper: { seed: 1 },
    palette: {
      name: 'moonlight',
      entries: [
        { role: 'base_wash', pigment: { name: 'indigo' } },
        { role: 'shadow', pigment: { name: 'paynes_grey' } },
      ],
    },
  }),
};

type Brush = { path: [number, number][]; radius: [number, number] };
type Dab = { center: [number, number]; radius: number };
type WashDocument = {
  size_hint: [number, number];
  palette: { entries: unknown[] };
  timeline: { total_ticks: number; events: { at_tick: number; op: { brush?: Brush; dab?: Dab; water?: Brush } | string }[] };
};

const brushes = (scene: WashDocument) =>
  scene.timeline.events.flatMap((event) => (typeof event.op !== 'string' && event.op.brush ? [event.op.brush] : []));

const wash = (width: number, height: number, seed: number) =>
  JSON.parse(reportWashScene(module, width, height, { seed })) as WashDocument;

describe('reportWashCrop', () => {
  it('shows the whole painting for a strip the grid can carry', () => {
    expect(reportWashCrop(120, 60)).toEqual({ x: 0, y: 0, width: 1, height: 1 });
  });

  it('shows a full-width band no more elongated than the grid for a long strip', () => {
    const crop = reportWashCrop(1100, 14);
    expect(crop.width).toBe(1);
    expect(crop.height).toBeCloseTo((14 * MAX_FRAME_ASPECT) / 1100);
    expect(crop.y + crop.height / 2).toBeCloseTo(0.5);
  });
});

describe('reportWashScene', () => {
  it('keeps every mark within the engine bounds and inside the shown band', () => {
    const invalid: unknown[] = [];
    for (const [width, height] of [[1100, 56], [1100, 14], [600, 40], [120, 40], [80, 120]]) {
      const crop = reportWashCrop(width!, height!);
      for (let seed = 0; seed < 24; seed++) {
        const json = reportWashScene(module, width!, height!, { seed, dpr: 2 });
        expect(parseSceneDocument(json).version).toBe(1);
        const scene = JSON.parse(json) as WashDocument;
        expect(scene.size_hint[0] / scene.size_hint[1]).toBeLessThanOrEqual(MAX_FRAME_ASPECT + 0.01);
        for (const event of scene.timeline.events) {
          if (event.at_tick < 0 || event.at_tick > scene.timeline.total_ticks) invalid.push(event);
          if (typeof event.op === 'string') continue;
          const mark = event.op.brush ?? event.op.water;
          const points = mark ? mark.path : event.op.dab ? [event.op.dab.center] : [];
          const radii = mark ? mark.radius : event.op.dab ? [event.op.dab.radius] : [];
          for (const [x, y] of points) {
            if (!(x >= 0 && x <= 1 && y >= crop.y - 1e-9 && y <= crop.y + crop.height + 1e-9)) invalid.push(event);
          }
          for (const radius of radii) {
            if (!(radius > 0 && radius <= 1)) invalid.push(event);
          }
        }
      }
    }
    expect(invalid).toEqual([]);
  });

  it('lays pigment over most of the strip from the left and leaves the right clear', () => {
    const crop = reportWashCrop(1100, 56);
    for (let seed = 0; seed < 24; seed++) {
      const laid = brushes(wash(1100, 56, seed));
      const xs = laid.flatMap((brush) => brush.path.map(([x]) => x));
      expect(Math.min(...xs)).toBeLessThan(0.04);
      expect(Math.max(...xs)).toBeGreaterThanOrEqual(0.7);
      expect(Math.max(...xs)).toBeLessThanOrEqual(0.85);
      const heights = laid.map((brush) => (brush.path[0]![1] - crop.y) / crop.height);
      expect(Math.min(...heights)).toBeLessThan(0.2);
      expect(Math.max(...heights)).toBeGreaterThan(0.8);
    }
  });

  it('keeps only the base wash it lays', () => {
    expect(wash(1100, 56, 3).palette.entries).toEqual([{ role: 'base_wash', pigment: { name: 'indigo' } }]);
  });

  it('reproduces a seed and paints another seed differently', () => {
    const options = { seed: 7, dpr: 2 };
    expect(reportWashScene(module, 1100, 56, options)).toBe(reportWashScene(module, 1100, 56, options));
    expect(wash(1100, 56, 7).timeline).not.toEqual(wash(1100, 56, 8).timeline);
  });
});
