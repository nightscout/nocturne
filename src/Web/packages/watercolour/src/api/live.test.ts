import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EngineHost } from './engine-host';
import { MAX_UNPRESENTED_RENDERS, type PlayerOptions, createArtworkPlayer } from './playback';
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
    /** Frames the scheduler admitted this instance to advance in. */
    advances: 0,
    get tick() {
      return tick;
    },
    attach(canvas: HTMLCanvasElement, width: number, height: number) {
      canvas.width = width;
      canvas.height = height;
    },
    setCrop: vi.fn(),
    setBlendTicks: vi.fn(),
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
      instance.advances++;
      if (!playing || tick >= total) return false;
      owed += seconds * 25;
      const whole = Math.floor(owed);
      owed -= whole;
      const next = Math.min(total, tick + whole);
      const moved = next !== tick;
      tick = next;
      return moved;
    },
    tickForProgress: (progress: number) => Math.round(progress * total),
    seekTowardsTick(target: number, ticks: number) {
      playing = false;
      if (target < tick) tick = 0;
      const from = tick;
      tick = Math.min(target, tick + ticks);
      calls.push(`seek:${target}:${ticks}`);
      return tick - from;
    },
    seekProgress(progress: number) { tick = Math.round(progress * total); playing = false; },
    reset() { tick = 0; playing = false; },
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
    ticksDue: (seconds: number) => (playing ? Math.min(total - tick, Math.floor(owed + seconds * 25)) : 0),
    ticksDueAtProgress: (progress: number) => Math.max(0, Math.round(progress * total) - tick),
    currentTick: () => tick,
  };
  return instance;
}

function fakeHost(...instances: ReturnType<typeof fakeInstance>[]): EngineHost {
  return timedHost({}, ...instances);
}

/** A host whose engine reports these GPU timings. */
function timedHost(gpuTimings: { gpuTickMs?: number; gpuRenderMs?: number }, ...instances: ReturnType<typeof fakeInstance>[]): EngineHost {
  const engine = {
    createInstance: () => instances.shift(),
    onDeviceLost() {},
    stats: () => ({ liveInstances: 0, maxLiveInstances: 4, ...gpuTimings }),
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

/** Lets a player's startup run to where it waits on the scheduler. */
const idle = () => new Promise((resolve) => setTimeout(resolve, 0));

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

describe('cancelled live creation', () => {
  it('balances the lease without creating a scene after the engine await', async () => {
    let complete!: (module: WasmModule) => void;
    const module_ready = new Promise<WasmModule>(resolve => { complete = resolve; });
    const instance = fakeInstance(4);
    const create_instance = vi.fn(() => instance);
    const module = {
      default: async () => {},
      WatercolourEngine: { create: async () => ({ createInstance: create_instance, onDeviceLost() {}, stats: () => ({ liveInstances: 0, maxLiveInstances: 4 }) }) },
      catalogueScene: () => '{"version":1}',
    } as unknown as WasmModule;
    const engine_host = new EngineHost({ loadModule: () => module_ready, capabilities: gpu });
    const { scheduler } = manualScheduler();
    const live = player(instance, scheduler, { engineHost: engine_host });
    await idle();
    expect(engine_host.refCount).toBe(1);
    live.dispose();
    complete(module);
    await live.ready;
    expect(create_instance).not.toHaveBeenCalled();
    expect(engine_host.refCount).toBe(0);
    const next = player(instance, scheduler, { engineHost: engine_host });
    await next.ready;
    expect(next.state.mode).toBe('live');
    next.dispose();
    expect(engine_host.refCount).toBe(0);
  });

  it('ends a cancelled queued still turn before taking an engine lease', async () => {
    const instance = fakeInstance(4);
    const engine_host = fakeHost(instance);
    const end_turn = await engine_host.stillTurn();
    const { scheduler } = manualScheduler();
    const live = player(instance, scheduler, { engineHost: engine_host, releaseAfterFinish: true, motion: 'reduced' });
    await idle(); live.dispose(); end_turn();
    await live.ready;
    expect(engine_host.refCount).toBe(0);
    expect(instance.calls).toEqual([]);
    const next_turn = await engine_host.stillTurn();
    next_turn();
  });
});

describe('presented progress', () => {
  it('paints the requested source window into the visible backing size', async () => {
    const instance = fakeInstance(1);
    const { scheduler } = manualScheduler();
    const crop = { x: 0.2, y: 0.3, width: 0.5, height: 0.4 };
    const live = player(instance, scheduler, { width: 400, height: 150, crop });
    await live.ready;
    expect(live.state.mode).toBe('live');
    expect(instance.setCrop).toHaveBeenCalledWith(0.2, 0.3, 0.5, 0.4);
    expect(live.canvas.width).toBe(400);
    expect(live.canvas.height).toBe(150);
    expect(live.state.detail).toBe('extraLarge');
    live.dispose();
  });

  it('blends ticks only for a player that asks', async () => {
    const blended = fakeInstance(1);
    const plain = fakeInstance(1);
    const { scheduler } = manualScheduler();
    const players = [player(blended, scheduler, { blendTicks: true }), player(plain, scheduler, {})];
    await Promise.all(players.map((live) => live.ready));
    expect(blended.setBlendTicks).toHaveBeenCalledWith(true);
    expect(plain.setBlendTicks).not.toHaveBeenCalled();
    for (const live of players) live.dispose();
  });

  it('rejects a crop outside the authored painting before acquiring an engine', () => {
    const instance = fakeInstance(1);
    const { scheduler } = manualScheduler();
    expect(() => player(instance, scheduler, { crop: { x: 0.9, y: 0, width: 0.2, height: 1 } })).toThrow(TypeError);
  });

  it('allows a progress callback to dispose the player before finish', async () => {
    const instance = fakeInstance(1);
    const { scheduler, frame } = manualScheduler();
    let live: ReturnType<typeof player>;
    live = player(instance, scheduler, { onProgress: () => live.dispose() });
    await live.ready;
    const finished = vi.fn();
    live.on('finished', finished);
    instance.isFinished = () => {
      if (instance.calls.includes('dispose')) throw new Error('freed instance');
      return instance.tick >= 1;
    };
    expect(() => { for (let i = 0; i < 5; i++) frame(); }).not.toThrow();
    expect(instance.calls).toContain('dispose');
    expect(finished).not.toHaveBeenCalled();
  });

  it('crosses the coverage threshold before finishing without extra state notifications', async () => {
    const instance = fakeInstance(40);
    const { scheduler, frame } = manualScheduler();
    const progress: number[] = [];
    const live = player(instance, scheduler, { onProgress: (p) => progress.push(p) });
    await live.ready;
    const statechange = vi.fn();
    live.on('statechange', statechange);
    for (let i = 0; i < 90 && !progress.some((p) => p >= 0.67); i++) frame();
    expect(progress.some((p) => p >= 0.67 && p < 1)).toBe(true);
    expect(live.state.finished).toBe(false);
    expect(statechange).not.toHaveBeenCalled();
    live.dispose();
  });

  it('withholds progress while a swapchain frame cannot be presented', async () => {
    const instance = fakeInstance(40);
    instance.render = () => false;
    const { scheduler, frame } = manualScheduler();
    const onProgress = vi.fn();
    const live = player(instance, scheduler, { onProgress });
    await live.ready;
    for (let i = 0; i < 5; i++) frame();
    expect(onProgress).not.toHaveBeenCalled();
    instance.render = () => true;
    frame();
    expect(onProgress).toHaveBeenCalledWith(instance.tick / 40);
    live.dispose();
  });
});

describe('a live still under reduced motion', () => {
  it('runs its timeline a budgeted slice per frame, then presents once and lets go', async () => {
    // A tick costs 1 ms of a 10 ms budget; each call is sized from the last.
    const instance = fakeInstance(40);
    const { scheduler, frame, spend } = manualScheduler();
    const advance = instance.advanceTicks;
    instance.advanceTicks = (ticks) => (spend(ticks), advance(ticks));
    const still = player(instance, scheduler, { motion: 'reduced', releaseAfterFinish: true });
    let finished = 0;
    still.on('finished', () => (finished += 1));
    await idle();
    expect(instance.calls).toEqual([]);

    // It is let in by a frame and starts on what that frame has left.
    frame();
    await still.ready;
    expect(still.state.mode).toBe('live');
    expect(instance.calls).toEqual(['ticks:4', 'ticks:6']);
    frame();
    expect(instance.calls.slice(2)).toEqual(['ticks:8', 'ticks:2']);

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
    player(first, scheduler, options);
    const b = player(second, scheduler, options);
    await idle();

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
    await idle();

    frame();
    await a.ready;
    await idle();
    expect(first.calls.at(-1)).toBe('dispose');
    expect(second.calls).toEqual([]);

    frame();
    await b.ready;
    expect(second.calls.slice(-2)).toEqual(['render@8', 'dispose']);
  });

  it('holds on to its instance until the finished frame is actually presented', async () => {
    const instance = fakeInstance(4);
    const { scheduler, frame } = manualScheduler();
    const render = instance.render;
    let swapchain = false;
    instance.render = () => (swapchain ? render() : false);
    const still = player(instance, scheduler, { motion: 'reduced', releaseAfterFinish: true });
    await idle();

    frame();
    await still.ready;
    expect(instance.calls).toEqual(['ticks:4']);
    expect(still.state.released).toBe(false);

    swapchain = true;
    frame();
    expect(instance.calls.slice(1)).toEqual(['render@4', 'dispose']);
    expect(still.state.released).toBe(true);
  });

  it('lets go of a canvas that never gets a texture, so the stills behind it run', async () => {
    const first = fakeInstance(4);
    const second = fakeInstance(4);
    first.render = () => (first.calls.push('render:none'), false);
    const { scheduler, frame } = manualScheduler();
    const engineHost = fakeHost(first, second);
    const options: PlayerOptions = { motion: 'reduced', releaseAfterFinish: true, engineHost };
    const a = player(first, scheduler, options);
    const b = player(second, scheduler, options);
    await idle();

    for (let i = 0; i < MAX_UNPRESENTED_RENDERS + 4 && !b.state.released; i++) {
      frame();
      await idle();
    }

    expect(first.calls.filter((c) => c === 'render:none')).toHaveLength(MAX_UNPRESENTED_RENDERS);
    expect(first.calls.at(-1)).toBe('dispose');
    expect(a.state.released).toBe(true);
    expect(second.calls.slice(-2)).toEqual(['render@4', 'dispose']);
    expect(b.state.released).toBe(true);
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


describe('target seeking', () => {
  it('coalesces requests and catches up in bounded frames before resuming play', async () => {
    const instance = fakeInstance(100);
    const clock = manualScheduler();
    const live = player(instance, clock.scheduler, { autoplay: 'never' });
    await live.ready;
    for (let i = 1; i <= 100; i++) live.seekTo(i / 100);
    expect(instance.tick).toBe(0);
    expect(instance.calls.filter(call => call.startsWith('seek:'))).toEqual([]);
    live.seekTo(0.6);
    live.play();
    clock.frame();
    expect(instance.tick).toBeGreaterThan(0);
    expect(instance.tick).toBeLessThanOrEqual(8);
    expect(live.state.playing).toBe(false);
    for (let i = 0; i < 100 && !live.state.playing; i++) clock.frame();
    expect(instance.tick).toBe(60);
    expect(live.state.playing).toBe(true);
    expect(instance.calls.filter(call => call.startsWith('seek:')).every(call => call.startsWith('seek:60:'))).toBe(true);
    live.dispose();
  });

  it('replaces unfinished work with a backwards target and stops at that target', async () => {
    const instance = fakeInstance(100);
    const clock = manualScheduler();
    const live = player(instance, clock.scheduler, { autoplay: 'never' });
    await live.ready;
    live.seekTo(0.9);
    for (let i = 0; i < 8; i++) clock.frame();
    expect(instance.tick).toBeGreaterThan(10);
    live.seekTo(0.1);
    for (let i = 0; i < 100 && instance.tick !== 10; i++) clock.frame();
    expect(instance.tick).toBe(10);
    const calls = instance.calls.length;
    for (let i = 0; i < 5; i++) clock.frame();
    expect(instance.calls.length).toBe(calls);
    live.seekTo(0.8);
    live.dispose();
    clock.frame();
    expect(instance.calls.at(-1)).toBe('dispose');
  });

  it('keeps a seek pending until a frame can actually be presented', async () => {
    const instance = fakeInstance(100);
    const render = vi.spyOn(instance, 'render').mockReturnValue(false);
    const clock = manualScheduler();
    const live = player(instance, clock.scheduler, { autoplay: 'never' });
    await live.ready;
    live.seekTo(0.1);
    for (let i = 0; i < MAX_UNPRESENTED_RENDERS + 10; i++) clock.frame();
    expect(instance.tick).toBe(10);
    expect(live.state.seeking).toBe(true);
    render.mockReturnValue(true);
    clock.frame();
    expect(live.state.seeking).toBe(false);
    live.dispose();
  });

  it('does not read a freed instance after a seek fault with easing', async () => {
    const instance = fakeInstance(100);
    vi.spyOn(instance, 'seekTowardsTick').mockImplementation(() => { throw new Error('seek failed'); });
    const progress = vi.spyOn(instance, 'progress').mockImplementation(() => {
      if (instance.calls.includes('dispose')) throw new Error('freed instance');
      return 0;
    });
    const clock = manualScheduler();
    const live = player(instance, clock.scheduler, { autoplay: 'never', easing: t => t });
    await live.ready;
    live.seekTo(0.5);
    progress.mockClear();
    expect(() => clock.frame()).not.toThrow();
    expect(instance.calls).toContain('dispose');
    expect(progress).not.toHaveBeenCalled();
    live.dispose();
  });

  it('lets immediate seek, reset and finish replace queued work', async () => {
    const instance = fakeInstance(100);
    const clock = manualScheduler();
    const live = player(instance, clock.scheduler, { autoplay: 'never' });
    await live.ready;
    live.seekTo(0.9); live.seek(0.2); clock.frame();
    expect(instance.tick).toBe(20);
    live.seekTo(0.9); live.reset(); clock.frame();
    expect(instance.tick).toBe(0);
    live.seekTo(0.2); live.finishImmediately(); clock.frame();
    expect(instance.tick).toBe(100);
    live.dispose();
  });
});

describe('GPU reservation', () => {
  it('admits two playing reveals every frame when the ticks they run fit the budget', async () => {
    const reveals = [fakeInstance(1000), fakeInstance(1000)];
    const { scheduler, frame } = manualScheduler();
    const timings = { gpuTickMs: 1, gpuRenderMs: 2 };
    const players = reveals.map((instance) => player(instance, scheduler, { engineHost: timedHost(timings, instance) }));
    await Promise.all(players.map((live) => live.ready));
    for (let i = 0; i < 30; i++) frame();
    expect(reveals.map((instance) => instance.advances)).toEqual([30, 30]);
    for (const live of players) live.dispose();
  });
});
