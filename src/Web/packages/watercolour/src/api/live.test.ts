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
  let owed = 0;
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
    /** 25 ticks a second: at 60 fps most frames run no tick at all. */
    advanceByElapsed(seconds: number) {
      if (!playing || tick >= total) return false;
      owed += seconds * 25;
      const whole = Math.floor(owed);
      owed -= whole;
      const next = Math.min(total, tick + whole);
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

function fakeHost(...instances: ReturnType<typeof fakeInstance>[]): EngineHost {
  const engine = {
    createInstance: () => instances.shift(),
    onDeviceLost() {},
    stats: () => ({ liveInstances: 0, maxLiveInstances: 4 }),
    maxLiveInstances: 4,
  };
  const module = {
    default: async () => {},
    WatercolourEngine: { create: async () => engine },
    catalogueScene: () => '{"version":1}',
  } as unknown as WasmModule;
  return new EngineHost({ loadModule: async () => module, capabilities: gpu });
}

const gpu = async () => ({ webgpu: true, adapter: true, reducedMotion: false, offscreenCanvas: false });

function manualScheduler() {
  const frames = new Map<number, (time: number) => void>();
  let clock = 0;
  let vsync = 0;
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
    /** Main-thread time an engine call takes. */
    spend(ms: number) {
      clock += ms;
    },
    /** The next display frame, 16 ms after the last; later if the work before it ran past that. */
    frame() {
      vsync += 16;
      clock = Math.max(clock, vsync);
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
    // A tick costs 1 ms of a 10 ms budget (9.6 ms once 16 ms frames are measured); each call is sized from the last.
    const instance = fakeInstance(40);
    const { scheduler, frame, spend } = manualScheduler();
    const advance = instance.advanceTicks;
    instance.advanceTicks = (ticks) => (spend(ticks), advance(ticks));
    const still = player(instance, scheduler, { motion: 'reduced', releaseAfterFinish: true });
    await still.ready;
    let finished = 0;
    still.on('finished', () => (finished += 1));

    expect(still.state.mode).toBe('live');
    expect(instance.calls).toEqual([]);

    frame();
    expect(instance.calls).toEqual(['ticks:4', 'ticks:6']);
    frame();
    expect(instance.calls.slice(2)).toEqual(['ticks:9']);

    for (let i = 0; i < 10 && !instance.calls.includes('dispose'); i++) frame();

    expect(instance.calls).not.toContain('finishImmediately');
    expect(instance.calls.filter((c) => c.startsWith('render'))).toEqual(['render@40']);
    expect(instance.calls.at(-1)).toBe('dispose');
    expect(finished).toBe(1);
    expect(still.state).toMatchObject({ finished: true, released: true });
    expect((window as Window).__watercolourLive).toEqual([]);
  });

  it('hands its turn to the next still inside the frame it finished in', async () => {
    const first = fakeInstance(8);
    const second = fakeInstance(8);
    const { scheduler, frame, spend } = manualScheduler();
    for (const instance of [first, second]) {
      const advance = instance.advanceTicks;
      instance.advanceTicks = (ticks) => (spend(1), advance(ticks));
    }
    const engineHost = fakeHost(first, second);
    const options: PlayerOptions = { motion: 'reduced', releaseAfterFinish: true, engineHost };
    const a = player(first, scheduler, options);
    const b = player(second, scheduler, options);
    await a.ready;

    frame();
    await b.ready;

    expect(first.calls.at(-1)).toBe('dispose');
    expect(second.calls.slice(-2)).toEqual(['render@8', 'dispose']);
    expect(b.state).toMatchObject({ finished: true, released: true });
  });

  it('leaves the next still for the next frame when the budget is spent', async () => {
    const first = fakeInstance(4);
    const second = fakeInstance(8);
    const { scheduler, frame, spend } = manualScheduler();
    const advance = first.advanceTicks;
    first.advanceTicks = (ticks) => (spend(10), advance(ticks));
    const engineHost = fakeHost(first, second);
    const options: PlayerOptions = { motion: 'reduced', releaseAfterFinish: true, engineHost };
    const a = player(first, scheduler, options);
    const b = player(second, scheduler, options);
    await a.ready;

    frame();
    await b.ready;
    expect(first.calls.at(-1)).toBe('dispose');
    expect(second.calls).toEqual([]);

    frame();
    expect(second.calls.slice(-2)).toEqual(['render@8', 'dispose']);
  });

  it('holds on to its instance until the finished frame is actually presented', async () => {
    const instance = fakeInstance(4);
    const { scheduler, frame } = manualScheduler();
    const render = instance.render;
    let swapchain = false;
    instance.render = () => (swapchain ? render() : false);
    const still = player(instance, scheduler, { motion: 'reduced', releaseAfterFinish: true });
    await still.ready;

    frame();
    expect(instance.calls).toEqual(['ticks:4']);
    expect(still.state.released).toBe(false);

    swapchain = true;
    frame();
    expect(instance.calls.slice(1)).toEqual(['render@4', 'dispose']);
    expect(still.state.released).toBe(true);
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

describe('a live reveal', () => {
  it('presents only the frames whose simulation moved', async () => {
    const instance = fakeInstance(8);
    const { scheduler, frame } = manualScheduler();
    const reveal = player(instance, scheduler, { motion: 'full', autoplay: 'once' });
    await reveal.ready;

    const moved: number[] = [];
    for (let i = 0; i < 40 && !reveal.state.finished; i++) {
      const before = instance.tick;
      frame();
      if (instance.tick !== before) moved.push(instance.tick);
    }

    const renders = instance.calls.filter((c) => c.startsWith('render'));
    // The first frame draws the blank sheet; after that only a moved tick does.
    expect(renders).toEqual(['render@0', ...moved.map((t) => `render@${t}`)]);
    expect(reveal.state.finished).toBe(true);
  });
});
