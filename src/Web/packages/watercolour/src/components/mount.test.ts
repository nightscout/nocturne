import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const created: Array<{ source: { surface?: string }; options: { width: number; height: number } }> = [];

vi.mock('../api/playback', () => ({
  createArtworkPlayer: (_canvas: unknown, source: { surface?: string }, options: { width: number; height: number }) => {
    created.push({ source, options });
    return { on: () => () => {}, dispose: () => {}, resize: () => {}, state: { released: false } };
  },
}));

import { measuredSize, mountPlayer } from './helpers';

type Resize = (entries: Array<{ contentRect: { width: number; height: number } }>) => void;

let observed: Resize | undefined;

function fakeFrame(width: number, height: number): HTMLElement {
  const canvas = { style: {}, width: 0, height: 0 };
  return {
    getBoundingClientRect: () => ({ width, height }),
    querySelector: () => canvas,
  } as unknown as HTMLElement;
}

const canvas = { style: {}, width: 0, height: 0 } as unknown as HTMLCanvasElement;

beforeEach(() => {
  created.length = 0;
  observed = undefined;
  vi.stubGlobal(
    'ResizeObserver',
    class {
      constructor(callback: Resize) {
        observed = callback;
      }
      observe() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('measuredSize', () => {
  it('has no size for a box with no area', () => {
    expect(measuredSize(0, 0)).toBeUndefined();
    expect(measuredSize(160, 0)).toBeUndefined();
    expect(measuredSize(0.4, 32)).toBeUndefined();
  });

  it('rounds a real box to whole pixels', () => {
    expect(measuredSize(159.6, 32.2)).toEqual({ width: 160, height: 32 });
  });
});

describe('mountPlayer', () => {
  it('creates no player while the frame has no area, then one at its first real size', () => {
    mountPlayer(fakeFrame(0, 0), canvas, 'header-motif', { surface: 'light' });
    expect(created).toHaveLength(0);

    observed!([{ contentRect: { width: 0, height: 0 } }]);
    expect(created).toHaveLength(0);

    observed!([{ contentRect: { width: 160, height: 32 } }]);
    expect(created).toHaveLength(1);
    expect(created[0].options.width).toBe(160);
    expect(created[0].options.height).toBe(32);
  });

  it('creates the player at once for a frame that already has area', () => {
    mountPlayer(fakeFrame(160, 32), canvas, 'header-motif', { surface: 'light' });
    expect(created).toHaveLength(1);
  });

  it('hands the surface it is given to the player', () => {
    mountPlayer(fakeFrame(96, 32), canvas, 'confirmation-background', { surface: 'dark' });
    expect(created[0].source.surface).toBe('dark');
  });
});
