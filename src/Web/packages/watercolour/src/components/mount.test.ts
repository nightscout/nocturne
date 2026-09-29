import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

type CreatedOptions = { width: number; height: number; startFinished?: boolean };

const created: Array<{ source: { surface?: string }; options: CreatedOptions; disposed: boolean }> = [];
let released = false;
let finished = false;

vi.mock('../api/playback', () => ({
  createArtworkPlayer: (_canvas: unknown, source: { surface?: string }, options: CreatedOptions) => {
    const entry = { source, options, disposed: false };
    created.push(entry);
    return {
      on: () => () => {},
      dispose: () => (entry.disposed = true),
      resize: () => {},
      get state() {
        return { released, finished };
      },
    };
  },
}));

import { setPresentation } from '../api/presentation';
import { measuredSize, mountPlayer, needsRepaint } from './helpers';

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

const unmounts: Array<() => void> = [];
const mount = (...args: Parameters<typeof mountPlayer>) => void unmounts.push(mountPlayer(...args));

beforeEach(() => {
  created.length = 0;
  released = false;
  finished = false;
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
  for (const unmount of unmounts.splice(0)) unmount();
  vi.unstubAllGlobals();
  vi.useRealTimers();
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
    mount(fakeFrame(0, 0), canvas, 'header-motif', { surface: 'light' });
    expect(created).toHaveLength(0);

    observed!([{ contentRect: { width: 0, height: 0 } }]);
    expect(created).toHaveLength(0);

    observed!([{ contentRect: { width: 160, height: 32 } }]);
    expect(created).toHaveLength(1);
    expect(created[0].options.width).toBe(160);
    expect(created[0].options.height).toBe(32);
  });

  it('creates the player at once for a frame that already has area', () => {
    mount(fakeFrame(160, 32), canvas, 'header-motif', { surface: 'light' });
    expect(created).toHaveLength(1);
  });

  it('hands the surface it is given to the player', () => {
    mount(fakeFrame(96, 32), canvas, 'confirmation-background', { surface: 'dark' });
    expect(created[0].source.surface).toBe('dark');
  });
});

describe('a change of presentation', () => {
  afterEach(() => setPresentation('animated'));

  it('rebuilds a finished player finished, and one mid-reveal from the start', () => {
    mount(fakeFrame(64, 64), canvas, 'avatar-wash', { surface: 'light' });
    setPresentation('still');
    expect(created).toHaveLength(2);
    expect(created[0].disposed).toBe(true);
    expect(created[1].options.startFinished).toBe(false);

    finished = true;
    setPresentation('animated');
    expect(created).toHaveLength(3);
    expect(created[2].options.startFinished).toBe(true);
  });
});

describe('a released still that is resized', () => {
  it('only stretches for a small change, and paints again, finished, once a large one settles', () => {
    vi.useFakeTimers();
    mount(fakeFrame(64, 64), canvas, 'avatar-wash', { surface: 'light', releaseAfterFinish: true });
    released = true;

    observed!([{ contentRect: { width: 72, height: 72 } }]);
    vi.advanceTimersByTime(1000);
    expect(created).toHaveLength(1);

    observed!([{ contentRect: { width: 128, height: 128 } }]);
    observed!([{ contentRect: { width: 160, height: 160 } }]);
    vi.advanceTimersByTime(1000);
    expect(created).toHaveLength(2);
    expect(created[0].disposed).toBe(true);
    expect(created[1].options).toMatchObject({ width: 160, height: 160, startFinished: true });
  });

  it('asks for a repaint only past the scale threshold', () => {
    const box = { width: 64, height: 64, offsetX: 0, offsetY: 0 };
    expect(needsRepaint({ width: 64, height: 64 }, box, 1)).toBe(false);
    expect(needsRepaint({ width: 64, height: 64 }, { ...box, width: 90 }, 1)).toBe(false);
    expect(needsRepaint({ width: 64, height: 64 }, { ...box, width: 96 }, 1)).toBe(true);
    expect(needsRepaint({ width: 64, height: 64 }, box, 2)).toBe(true);
    expect(needsRepaint({ width: 128, height: 128 }, box, 1)).toBe(true);
  });
});
