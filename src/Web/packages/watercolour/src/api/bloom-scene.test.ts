import { describe, expect, it } from 'vitest';
import { bloomScene } from './bloom-scene';
import { parseSceneDocument } from './scenes';

const module = {
  catalogueScene: () => JSON.stringify({
    version: 1,
    paper: { seed: 1 },
    palette: { name: 'slate', entries: [{ role: 'base_wash', pigment: { name: 'slate' } }] },
  }),
};
const colour = [0, 150 / 255, 136 / 255] as const;

type Stroke = { path: [number, number][]; radius: [number, number] };
type Dab = { center: [number, number]; radius: number };
type BloomDocument = {
  sim_resolution: number;
  timeline: { total_ticks: number; events: { at_tick: number; op: { dab?: Dab; water?: Stroke } | string }[] };
};

const document = (width: number, height: number, seed: number, slope: number) =>
  JSON.parse(bloomScene(module, width, height, { seed, slope, colour })) as BloomDocument;

describe('bloomScene', () => {
  it('keeps charges within the engine bounds across sizes, slopes and seeds', () => {
    const invalid: unknown[] = [];
    for (const [width, height] of [[400, 150], [150, 400], [64, 64], [939, 61]]) {
      for (const slope of [-1, 0, 1]) {
        for (let seed = 0; seed < 24; seed++) {
          const json = bloomScene(module, width!, height!, { seed, slope, colour });
          expect(parseSceneDocument(json).version).toBe(1);
          const scene = JSON.parse(json) as BloomDocument;
          expect(scene.sim_resolution).toBeLessThanOrEqual(320);
          for (const event of scene.timeline.events) {
            if (event.at_tick < 0 || event.at_tick > scene.timeline.total_ticks) invalid.push(event);
            if (typeof event.op === 'string') continue;
            const points = event.op.dab ? [event.op.dab.center] : event.op.water?.path;
            const radii = event.op.dab ? [event.op.dab.radius] : event.op.water?.radius;
            if (!points || !radii) continue;
            for (const point of points) {
              for (const coordinate of point) {
                if (!Number.isFinite(coordinate) || coordinate < 0 || coordinate > 1) invalid.push(event);
              }
            }
            for (const radius of radii) {
              if (!Number.isFinite(radius) || radius <= 0 || radius > 1) invalid.push(event);
            }
          }
        }
      }
    }
    expect(invalid).toEqual([]);
  });

  it('lands left, middle, right with the slope of the reading', () => {
    for (let seed = 0; seed < 24; seed++) {
      for (const slope of [-1, 1]) {
        const charges = document(400, 150, seed, slope).timeline.events.flatMap((event) =>
          typeof event.op !== 'string' && event.op.dab ? [{ tick: event.at_tick, point: event.op.dab.center }] : [],
        );
        const first = [0, 1, 2].map((band) => charges
          .filter(({ point }) => Math.floor(point[0] * 3) === band)
          .sort((a, b) => a.tick - b.tick)[0]!);
        expect(first).toHaveLength(3);
        for (let i = 1; i < first.length; i++) {
          expect(first[i]!.tick).toBeGreaterThan(first[i - 1]!.tick);
          expect(first[i]!.point[0]).toBeGreaterThan(first[i - 1]!.point[0]);
          expect((first[i - 1]!.point[1] - first[i]!.point[1]) * slope).toBeGreaterThan(0);
        }
      }
    }
  });

  it('reproduces a reading seed and varies the next reading', () => {
    const options = { seed: 7, slope: 0.5, colour };
    expect(bloomScene(module, 400, 150, options)).toBe(bloomScene(module, 400, 150, options));
    expect(document(400, 150, 7, 0.5).timeline).not.toEqual(document(400, 150, 8, 0.5).timeline);
  });
});
