import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IconNode } from '../types';
import { EngineHost } from './engine-host';
import { type PlayerState, autoplayAction, createArtworkPlayer, iconStaticBackend } from './playback';
import { Scheduler } from './scheduler';

const clock: IconNode[] = [
  ['circle', { cx: '12', cy: '12', r: '10' }],
  ['path', { d: 'M12 6v6l4 2' }],
];

describe('iconStaticBackend', () => {
  it('draws the baked final for an icon that has one', () => {
    expect(iconStaticBackend({ icon: clock, name: 'database' }, true)).toBe('baked');
  });

  it('falls to the plain SVG for an icon that is not baked', () => {
    expect(iconStaticBackend({ icon: clock, name: 'clock' }, false)).toBe('svg');
  });

  it('never routes an artwork source to the SVG backend', () => {
    expect(iconStaticBackend(undefined, false)).toBe('baked');
  });
});

describe('player statechange', () => {
  const noopContext = new Proxy({}, { get: () => () => {}, set: () => true });
  const canvas = () =>
    ({ width: 64, height: 64, clientWidth: 64, clientHeight: 64, getContext: () => noopContext }) as unknown as HTMLCanvasElement;
  const capabilities = async () => ({ webgpu: false, adapter: false, reducedMotion: false, offscreenCanvas: false });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('fires on a natural finish with the player finished and stopped', async () => {
    const manifest = { version: 1, frames: 2, width: 4, height: 4, durationMs: 100, layout: 'vertical' };
    vi.stubGlobal('fetch', async (url: string) =>
      url === 'manifest' ? new Response(JSON.stringify(manifest)) : new Response(new Blob([])),
    );
    vi.stubGlobal('createImageBitmap', async () => ({ width: 4, height: 8, close() {} }));
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
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, {
      mode: 'baked',
      autoplay: 'never',
      durationMs: 100,
      width: 64,
      height: 64,
      dpr: 1,
      assets: { manifest: 'manifest', strip: 'strip' },
      capabilities,
      scheduler,
      engineHost: new EngineHost(),
    });
    await player.ready;
    const states: PlayerState[] = [];
    player.on('statechange', () => states.push(player.state));

    player.play();
    for (let i = 0; i < 20 && !player.state.finished; i++) {
      clock += 16;
      const pending = Array.from(frames.values());
      frames.clear();
      for (const cb of pending) cb(clock);
    }

    expect(states.at(-1)).toMatchObject({ mode: 'baked', finished: true, playing: false });
  });

  it('settles on none when it cannot start at all', async () => {
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, {
      capabilities: () => Promise.reject(new Error('probe failed')),
      engineHost: new EngineHost(),
    });
    const states: PlayerState[] = [];
    player.on('statechange', () => states.push(player.state));

    await player.ready;

    expect(states.at(-1)?.mode).toBe('none');
    expect(states.at(-1)?.error).toBeDefined();
  });
});

describe('autoplayAction', () => {
  it('reveals a releasing player that autoplays, so the slot frees only when the reveal ends', () => {
    expect(autoplayAction('full', undefined, true)).toBe('play');
    expect(autoplayAction('full', 'once', true)).toBe('play');
  });

  it('finishes at once under reduced motion, releasing or not', () => {
    expect(autoplayAction('reduced', 'once', true)).toBe('finish');
    expect(autoplayAction('reduced', 'once', false)).toBe('finish');
  });

  it('finishes a releasing player that never autoplays, so it cannot hold a slot waiting for play()', () => {
    expect(autoplayAction('full', 'never', true)).toBe('finish');
    expect(autoplayAction('full', 'never', false)).toBe('wait');
  });
});

describe('live stills after a failed engine boot', () => {
  it('ends a failed still turn, so the next still resolves', async () => {
    const host = new EngineHost({ loadModule: () => Promise.reject(new Error('wasm fetch failed')) });
    const capabilities = async () => ({ webgpu: true, adapter: true, reducedMotion: true, offscreenCanvas: false });
    const scheduler = { register: () => ({ setActive() {}, dispose() {}, visible: true }) } as never;
    const canvas = () => ({ clientWidth: 32, clientHeight: 32, width: 32, height: 32 }) as unknown as HTMLCanvasElement;
    const options = { engineHost: host, scheduler, capabilities, releaseAfterFinish: true, motion: 'reduced' as const, width: 32, height: 32, dpr: 1 };

    const first = createArtworkPlayer(canvas(), { sceneJson: '{}' }, options);
    await first.ready;
    expect(first.state.mode).toBe('none');
    expect(host.refCount).toBe(0);

    const second = createArtworkPlayer(canvas(), { sceneJson: '{}' }, options);
    const outcome = await Promise.race([
      second.ready.then(() => 'settled'),
      new Promise((resolve) => setTimeout(() => resolve('hung'), 500)),
    ]);
    expect(outcome).toBe('settled');
    expect(second.state.mode).toBe('none');
  });
});
