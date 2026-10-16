import { afterEach, describe, expect, it, vi } from 'vitest';
import { EngineHost } from './engine-host';
import { prefersReducedMotion } from './capabilities';
import { clearSharedStills } from './baked';
import { createArtworkPlayer } from './playback';
import { getPresentation, setPresentation, subscribePresentation } from './presentation';

afterEach(() => {
  setPresentation('animated');
  clearSharedStills();
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
  const scheduler = { register: () => ({ setActive() {}, dispose() {}, visible: true }), whenBudget: async () => {} } as never;

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

  describe('still', () => {
    const noopContext = new Proxy({}, { get: () => () => {}, set: () => true });
    const drawable = () =>
      ({ clientWidth: 32, clientHeight: 32, width: 32, height: 32, getContext: () => noopContext }) as unknown as HTMLCanvasElement;
    const stubStillFetch = () => {
      const fetched: string[] = [];
      vi.stubGlobal('fetch', async (url: string) => {
        fetched.push(url);
        return new Response(new Blob([]));
      });
      vi.stubGlobal('createImageBitmap', async () => ({ width: 128, height: 128, close() {} }));
      return fetched;
    };

    it('draws the static asset without probing the GPU or acquiring the engine', async () => {
      setPresentation('still');
      const fetched = stubStillFetch();
      const capabilities = vi.fn(gpu);
      const loadModule = vi.fn();
      const host = new EngineHost({ loadModule, capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { id: 'suitcase', seed: 7 }, {
        capabilities,
        scheduler,
        engineHost: host,
        releaseAfterFinish: true,
        assets: { final: '/final-512.webp', 'final-small': '/final-128.webp', strip: '/strip.webp', manifest: '/strip.json' },
        width: 32,
        height: 32,
        dpr: 1,
      });
      await player.ready;
      expect(player.state.mode).toBe('static');
      expect(fetched).toEqual(['/final-128.webp']);
      expect(capabilities).not.toHaveBeenCalled();
      expect(acquire).not.toHaveBeenCalled();
      expect(loadModule).not.toHaveBeenCalled();
    });

    it('renders one live frame for a scene with no asset of its own', async () => {
      setPresentation('still');
      const capabilities = vi.fn(gpu);
      const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no engine')), capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { sceneJson: '{"version":1}' }, {
        capabilities,
        scheduler,
        engineHost: host,
        width: 32,
        height: 32,
        dpr: 1,
      });
      await player.ready;
      expect(capabilities).toHaveBeenCalledOnce();
      expect(acquire).toHaveBeenCalledOnce();
      expect(player.state.motion).toBe('reduced');
    });

    it('settles a scene that opted out of a live still on none, probing and acquiring nothing', async () => {
      setPresentation('still');
      const capabilities = vi.fn(gpu);
      const host = new EngineHost({ loadModule: vi.fn(), capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { sceneJson: '{"version":1}' }, {
        capabilities,
        scheduler,
        engineHost: host,
        liveStill: false,
        width: 32,
        height: 32,
        dpr: 1,
      });
      await player.ready;
      expect(player.state.mode).toBe('none');
      expect(player.state.error).toBeUndefined();
      expect(capabilities).not.toHaveBeenCalled();
      expect(acquire).not.toHaveBeenCalled();
    });

    it('makes warm a no-op, so nothing boots the engine ahead of a still', async () => {
      setPresentation('still');
      const loadModule = vi.fn();
      expect(await new EngineHost({ loadModule, capabilities: gpu }).warm()).toBe(false);
      expect(loadModule).not.toHaveBeenCalled();
    });
  });

  describe('animated: still first, live once the engine is ready', () => {
    const noopContext = new Proxy({}, { get: () => () => {}, set: () => true });
    const drawable = () =>
      ({ clientWidth: 32, clientHeight: 32, width: 32, height: 32, getContext: () => noopContext }) as unknown as HTMLCanvasElement;
    const assets = { final: '/final-512.webp', 'final-small': '/final-128.webp', strip: '/strip.webp', manifest: '/strip.json' };
    const engine = {
      maxLiveInstances: 4,
      onDeviceLost() {},
      stats: () => ({ liveInstances: 0, maxLiveInstances: 4 }),
      createInstance: () => {
        throw new Error('stop at the instance');
      },
    };
    const module = { WatercolourEngine: { create: async () => engine } } as never;
    const stubStill = () => {
      const fetched: string[] = [];
      vi.stubGlobal('fetch', async (url: string) => {
        fetched.push(url);
        return new Response(new Blob([]));
      });
      vi.stubGlobal('createImageBitmap', async () => ({ width: 128, height: 128, close() {} }));
      return fetched;
    };

    it('draws the still before the engine is ready, probing and acquiring nothing', async () => {
      const fetched = stubStill();
      const capabilities = vi.fn(gpu);
      const host = new EngineHost({ loadModule: async () => module, capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { id: 'suitcase' }, { capabilities, scheduler, engineHost: host, assets, width: 32, height: 32, dpr: 1 });
      await player.ready;
      expect(player.state.mode).toBe('static');
      expect(fetched).toEqual(['/final-128.webp']);
      expect(capabilities).not.toHaveBeenCalled();
      expect(acquire).not.toHaveBeenCalled();
    });

    it('treats a boot in progress as not ready', async () => {
      stubStill();
      const host = new EngineHost({ loadModule: () => new Promise(() => {}), capabilities: gpu });
      void host.acquire();
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { id: 'suitcase' }, { capabilities: gpu, scheduler, engineHost: host, assets, width: 32, height: 32, dpr: 1 });
      await player.ready;
      expect(player.state.mode).toBe('static');
      expect(acquire).not.toHaveBeenCalled();
    });

    it('goes live for a player created once warm() has made the engine ready', async () => {
      stubStill();
      const host = new EngineHost({ loadModule: async () => module, capabilities: gpu });
      expect(await host.warm()).toBe(true);
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { id: 'suitcase' }, { capabilities: gpu, scheduler, engineHost: host, assets, width: 32, height: 32, dpr: 1 });
      await player.ready;
      expect(acquire).toHaveBeenCalledOnce();
    });

    it('still boots the engine for a source with no still', async () => {
      const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no engine')), capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { sceneJson: '{"version":1}' }, { capabilities: gpu, scheduler, engineHost: host, width: 32, height: 32, dpr: 1 });
      await player.ready;
      expect(acquire).toHaveBeenCalledOnce();
    });

    it('leaves an explicit live request alone', async () => {
      stubStill();
      const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no engine')), capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { id: 'suitcase' }, { mode: 'live', capabilities: gpu, scheduler, engineHost: host, assets, width: 32, height: 32, dpr: 1 });
      await player.ready;
      expect(acquire).toHaveBeenCalledOnce();
    });

    it('can be turned off, so a cold engine boots for the reveal', async () => {
      stubStill();
      const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no engine')), capabilities: gpu });
      const acquire = vi.spyOn(host, 'acquire');
      const player = createArtworkPlayer(drawable(), { id: 'suitcase' }, {
        stillUntilReady: false, capabilities: gpu, scheduler, engineHost: host, assets, width: 32, height: 32, dpr: 1,
      });
      await player.ready;
      expect(acquire).toHaveBeenCalledOnce();
    });
  });

  it('warm does nothing while off', async () => {
    setPresentation('off');
    const loadModule = vi.fn();
    expect(await new EngineHost({ loadModule }).warm()).toBe(false);
    expect(loadModule).not.toHaveBeenCalled();
  });
});
