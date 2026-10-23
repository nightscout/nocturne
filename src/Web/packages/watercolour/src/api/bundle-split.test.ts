import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/** Lookups in the map; `assets.ts` imports the module only to make one. */
let bundleLoads = 0;

vi.mock('./bundled-assets', async (importOriginal) => {
  const original = await importOriginal<typeof import('./bundled-assets')>();
  return {
    ...original,
    hasBundled: (...args: Parameters<typeof original.hasBundled>) => {
      bundleLoads += 1;
      return original.hasBundled(...args);
    },
    bundledUrl: (...args: Parameters<typeof original.bundledUrl>) => {
      bundleLoads += 1;
      return original.bundledUrl(...args);
    },
  };
});

const gpu = async () => ({ webgpu: true, adapter: true, reducedMotion: false, offscreenCanvas: false });
const noGpu = async () => ({ webgpu: false, adapter: false, reducedMotion: false, offscreenCanvas: false });
const scheduler = { register: () => ({ setActive() {}, dispose() {}, visible: true }), whenBudget: async () => {} } as never;
const noopContext = new Proxy({}, { get: () => () => {}, set: () => true });
const canvas = () =>
  ({ clientWidth: 32, clientHeight: 32, width: 32, height: 32, getContext: () => noopContext }) as unknown as HTMLCanvasElement;

async function load() {
  const [{ createArtworkPlayer }, { EngineHost }] = await Promise.all([import('./playback'), import('./engine-host')]);
  return { createArtworkPlayer, EngineHost };
}

beforeEach(() => {
  vi.resetModules();
  bundleLoads = 0;
  vi.stubGlobal('fetch', async () => new Response(new Blob([])));
  vi.stubGlobal('createImageBitmap', async () => ({ width: 128, height: 128, close() {} }));
});

afterEach(() => vi.unstubAllGlobals());

describe('the bundled-asset URL map', () => {
  it('is not loaded for an artwork that resolves live whatever its assets', async () => {
    // A cold engine host would draw the still first, which reads the map.
    const { createArtworkPlayer, EngineHost } = await load();
    const booting = { WatercolourEngine: { create: () => new Promise(() => {}) } };
    const host = new EngineHost({ loadModule: async () => booting as never, capabilities: gpu });
    const acquire = vi.spyOn(host, 'acquire');
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, {
      stillUntilReady: false, capabilities: gpu, scheduler, engineHost: host, width: 32, height: 32, dpr: 1,
    });
    await vi.waitFor(() => expect(acquire).toHaveBeenCalledOnce());
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(bundleLoads).toBe(0);
    player.dispose();
  });

  it('is loaded by a resolve that may draw a baked or static asset', async () => {
    const { createArtworkPlayer, EngineHost } = await load();
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, {
      capabilities: noGpu,
      scheduler,
      engineHost: new EngineHost({ capabilities: noGpu }),
      mode: 'static',
      width: 32,
      height: 32,
      dpr: 1,
    });
    await player.ready;
    expect(bundleLoads).toBeGreaterThan(0);
    expect(player.state.mode).toBe('static');
  });

  it('is loaded once live fails, for the fallback chain', async () => {
    const { createArtworkPlayer, EngineHost } = await load();
    const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no engine')), capabilities: gpu });
    const player = createArtworkPlayer(canvas(), { id: 'suitcase' }, { capabilities: gpu, scheduler, engineHost: host, width: 32, height: 32, dpr: 1 });
    await player.ready;
    expect(bundleLoads).toBeGreaterThan(0);
    expect(player.state.mode).not.toBe('live');
  });
});
