import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EngineHost } from './engine-host';
import { type PlayerOptions, createArtworkPlayer } from './playback';
import { Scheduler } from './scheduler';
import type { WasmModule } from './wasm-types';

/** A wasm instance whose timeline is `total` ticks, recording what the host asks of it. */
function fakeInstance(total: number) {
  const calls: string[] = [];
  let tick = 0;
  let playing = false;
  const instance = {
    calls,
    get tick() {
      return tick;
    },
    attach() {},
    setProgressCurve() {},
    play: () => void (playing = true),
    pause: () => void (playing = false),
    advanceTicks(ticks: number) {
      tick = Math.min(total, tick + ticks);
      calls.push(`ticks:${ticks}`);
      return tick >= total;
    },
    advanceByElapsed(seconds: number) {
      if (!playing || tick >= total) return false;
      const next = Math.min(total, tick + Math.floor(seconds * 100));
      const moved = next !== tick;
      tick = next;
      return moved;
    },
    finishImmediately() {
      calls.push('finishImmediately');
      tick = total;
    },
    isFinished: () => tick >= total,
    isPlaying: () => playing && tick < total,
    progress: () => tick / total,
    render() {
      calls.push(`render@${tick}`);
      return true;
    },
    dispose: () => void calls.push('dispose'),
    simResolution: () => 96,
    totalTicks: () => total,
  };
  return instance;
}

function fakeHost(instance: ReturnType<typeof fakeInstance>): EngineHost {
  const engine = {
    createInstance: () => instance,
    onDeviceLost() {},
    stats: () => ({ liveInstances: 0, maxLiveInstances: 4 }),
    maxLiveInstances: 4,
  };
  const module = {
    default: async () => {},
    WatercolourEngine: { create: async () => engine },
    catalogueScene: () => '{"version":1}',
  } as unknown as WasmModule;
  return new EngineHost({ loadModule: async () => module });
}

const gpu = async () => ({ webgpu: true, adapter: true, reducedMotion: false, offscreenCanvas: false });

function manualScheduler() {
  const frames = new Map<number, (time: number) => void>();
  let clock = 0;
  let handle = 0;
  const scheduler = new Scheduler({
    requestAnimationFrame: (cb) => {
      frames.set(++handle, cb);
      return handle;
    },
    cancelAnimationFrame: (h) => void frames.delete(h),
    now: () => clock,
  });
  return {
    scheduler,
    frame() {
      clock += 16;
      const pending = Array.from(frames.values());
      frames.clear();
      for (const cb of pending) cb(clock);
    },
  };
}

const canvas = () => ({ width: 64, height: 64, clientWidth: 64, clientHeight: 64 }) as unknown as HTMLCanvasElement;

function player(instance: ReturnType<typeof fakeInstance>, scheduler: Scheduler, options: PlayerOptions) {
  return createArtworkPlayer(canvas(), { id: 'avatar-wash', seed: 5 }, {
    width: 32,
    height: 32,
    dpr: 1,
    engineHost: fakeHost(instance),
    scheduler,
    capabilities: gpu,
    ...options,
  });
}

beforeEach(() => {
  // DEV builds list live instances on `window` for the browser audit.
  vi.stubGlobal('window', {});
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('a live still under reduced motion', () => {
  it('runs its timeline a budgeted slice per frame, then presents once and lets go', async () => {
    // Each engine call takes 4 ms of the 6 ms budget, so a frame runs two.
    let now = 0;
    vi.spyOn(performance, 'now').mockImplementation(() => (now += 4));
    const instance = fakeInstance(40);
    const { scheduler, frame } = manualScheduler();
    const still = player(instance, scheduler, { motion: 'reduced', releaseAfterFinish: true });
    await still.ready;
    let finished = 0;
    still.on('finished', () => (finished += 1));

    expect(still.state.mode).toBe('live');
    expect(instance.calls).toEqual([]);

    frame();
    expect(instance.calls).toEqual(['ticks:4', 'ticks:4']);

    for (let i = 0; i < 10 && !instance.calls.includes('dispose'); i++) frame();

    expect(instance.calls).not.toContain('finishImmediately');
    expect(instance.calls.filter((c) => c.startsWith('render'))).toEqual(['render@40']);
    expect(instance.calls.at(-1)).toBe('dispose');
    expect(finished).toBe(1);
    expect(still.state).toMatchObject({ finished: true, released: true });
  });

  it('still finishes in one call when the host asks for it outright', async () => {
    const instance = fakeInstance(40);
    const { scheduler } = manualScheduler();
    const pinned = player(instance, scheduler, { motion: 'full', autoplay: 'never' });
    await pinned.ready;

    pinned.finishImmediately();

    expect(instance.calls).toEqual(['finishImmediately']);
    expect(pinned.state.finished).toBe(true);
  });
});
