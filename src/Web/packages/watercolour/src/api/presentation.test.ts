import { afterEach, describe, expect, it, vi } from 'vitest';
import { EngineHost } from './engine-host';
import { prefersReducedMotion } from './capabilities';
import { createArtworkPlayer } from './playback';
import { getPresentation, setPresentation, subscribePresentation } from './presentation';

afterEach(() => {
  setPresentation('animated');
  vi.unstubAllGlobals();
});

describe('presentation preference', () => {
  it('defaults to animated', () => {
    expect(getPresentation()).toBe('animated');
  });

  it('notifies subscribers only when the value changes, until they unsubscribe', () => {
    const seen: string[] = [];
    const off = subscribePresentation((value) => seen.push(value));
    setPresentation('still');
    setPresentation('still');
    setPresentation('off');
    off();
    setPresentation('animated');
    expect(seen).toEqual(['still', 'off']);
  });

  it('makes prefersReducedMotion true for still and off', () => {
    vi.stubGlobal('matchMedia', () => ({ matches: false }));
    expect(prefersReducedMotion()).toBe(false);
    setPresentation('still');
    expect(prefersReducedMotion()).toBe(true);
    setPresentation('off');
    expect(prefersReducedMotion()).toBe(true);
  });
});

describe('playback under a presentation', () => {
  const canvas = () => ({ clientWidth: 32, clientHeight: 32, width: 32, height: 32 }) as unknown as HTMLCanvasElement;
  const gpu = async () => ({ webgpu: true, adapter: true, reducedMotion: false, offscreenCanvas: false });
  const scheduler = { register: () => ({ setActive() {}, dispose() {}, visible: true }) } as never;

  it('off settles on none without probing or fetching anything', async () => {
    setPresentation('off');
    const fetcher = vi.fn();
    vi.stubGlobal('fetch', fetcher);
    const capabilities = vi.fn(gpu);
    const loadModule = vi.fn();
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, {
      capabilities,
      scheduler,
      engineHost: new EngineHost({ loadModule }),
      width: 32,
      height: 32,
      dpr: 1,
    });
    let notified = 0;
    player.on('statechange', () => notified++);
    await player.ready;
    expect(player.state.mode).toBe('none');
    expect(notified).toBeGreaterThan(0);
    expect(capabilities).not.toHaveBeenCalled();
    expect(fetcher).not.toHaveBeenCalled();
    expect(loadModule).not.toHaveBeenCalled();
  });

  it('still resolves reduced motion and never a playing live scene', async () => {
    setPresentation('still');
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, {
      motion: 'full',
      mode: 'baked',
      capabilities: gpu,
      scheduler,
      engineHost: new EngineHost({ loadModule: () => Promise.reject(new Error('no engine')) }),
      assets: {},
      width: 32,
      height: 32,
      dpr: 1,
    });
    await player.ready;
    expect(player.state.motion).toBe('reduced');
    expect(player.state.mode).not.toBe('live');
    expect(player.state.playing).toBe(false);
  });

  it('warm does nothing while off', async () => {
    setPresentation('off');
    const loadModule = vi.fn();
    expect(await new EngineHost({ loadModule }).warm()).toBe(false);
    expect(loadModule).not.toHaveBeenCalled();
  });
});
