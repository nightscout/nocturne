import { describe, expect, it, vi } from 'vitest';
import { EngineHost, answeringAdapterRequest } from './engine-host';
import type { WasmEngine, WasmModule } from './wasm-types';

const gpu = async () => ({ webgpu: true, adapter: true, reducedMotion: false, offscreenCanvas: false });

function fakeModule(): { module: WasmModule; created: () => number } {
  let created = 0;
  const engine = {
    free: () => {},
    createInstance: () => ({}) as never,
    isLost: () => false,
    onDeviceLost: () => {},
    adapterName: () => 'fake',
    stats: () => ({ liveInstances: 0, maxLiveInstances: 4 }) as never,
    maxLiveInstances: 4,
  } satisfies Partial<WasmEngine> as unknown as WasmEngine;
  const module = {
    WatercolourEngine: {
      create: async () => {
        created += 1;
        return engine;
      },
    },
  } as unknown as WasmModule;
  return { module, created: () => created };
}

describe('EngineHost.warm (paying the boot before a pointer does)', () => {
  it('builds the engine and hands the slot straight back', async () => {
    const { module, created } = fakeModule();
    const host = new EngineHost({ loadModule: async () => module, capabilities: gpu });

    expect(await host.warm()).toBe(true);
    expect(host.ready).toBe(true);
    // A warm-up that kept its lease would spend one of the four live slots
    // on nothing, and the fourth real mark would fall back to a still.
    expect(host.refCount).toBe(0);
    expect(created()).toBe(1);
  });

  it('does not build a second engine for the first real mark', async () => {
    const { module, created } = fakeModule();
    const host = new EngineHost({ loadModule: async () => module, capabilities: gpu });
    await host.warm();
    await host.acquire();
    expect(created()).toBe(1);
    expect(host.refCount).toBe(1);
  });

  it('reports no GPU rather than throwing, because the still covers it', async () => {
    const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no adapter')), capabilities: gpu });
    expect(await host.warm()).toBe(false);
    expect(host.ready).toBe(false);
    expect(host.refCount).toBe(0);
  });

  it('leaves a failed warm-up retryable', async () => {
    const loadModule = vi
      .fn<() => Promise<WasmModule>>()
      .mockRejectedValueOnce(new Error('no adapter'))
      .mockImplementation(async () => fakeModule().module);
    const host = new EngineHost({ loadModule, capabilities: gpu });

    expect(await host.warm()).toBe(false);
    expect(await host.warm()).toBe(true);
    expect(loadModule).toHaveBeenCalledTimes(2);
  });
});

describe('EngineHost.warm without a GPU', () => {
  it('asks for an adapter before fetching the module, and fetches nothing without one', async () => {
    const loadModule = vi.fn(async () => fakeModule().module);
    for (const capabilities of [
      async () => ({ webgpu: false, adapter: false, reducedMotion: false, offscreenCanvas: false }),
      async () => ({ webgpu: true, adapter: false, reducedMotion: false, offscreenCanvas: false }),
    ]) {
      const host = new EngineHost({ loadModule, capabilities });
      expect(await host.warm()).toBe(false);
      await expect(host.acquire()).rejects.toMatchObject({ code: 'WebGpuUnavailable' });
      host.release();
    }
    expect(loadModule).not.toHaveBeenCalled();
  });
});

describe('EngineHost.warmWhenIdle', () => {
  it('waits for an idle period, then warms', async () => {
    const { module, created } = fakeModule();
    let idle: (() => void) | undefined;
    vi.stubGlobal('requestIdleCallback', (cb: () => void) => void (idle = cb));
    const host = new EngineHost({ loadModule: async () => module, capabilities: gpu });

    const warmed = host.warmWhenIdle();
    await Promise.resolve();
    expect(created()).toBe(0);

    idle!();
    expect(await warmed).toBe(true);
    expect(created()).toBe(1);
    vi.unstubAllGlobals();
  });
});

describe('EngineHost.stillTurn', () => {
  it('hands out one turn at a time, in the order asked', async () => {
    const host = new EngineHost();
    const order: string[] = [];
    const first = host.stillTurn().then((end) => {
      order.push('first');
      return end;
    });
    const second = host.stillTurn().then((end) => {
      order.push('second');
      end();
    });

    const endFirst = await first;
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(order).toEqual(['first']);

    endFirst();
    await second;
    expect(order).toEqual(['first', 'second']);
  });
});

describe('the adapter the capability probe was handed', () => {
  function stubGpu() {
    const asked: unknown[] = [];
    const gpu = {
      requestAdapter: (options?: unknown) => {
        asked.push(options);
        return Promise.resolve('fresh adapter');
      },
    };
    vi.stubGlobal('navigator', { gpu });
    return { gpu, asked };
  }

  it('answers the engine`s own request, once, and then gets out of the way', async () => {
    const { gpu, asked } = stubGpu();
    const own = gpu.requestAdapter;
    const got: unknown[] = [];
    const engine = await answeringAdapterRequest('probed adapter', async () => {
      got.push(await (navigator as unknown as { gpu: typeof gpu }).gpu.requestAdapter({ powerPreference: 'high-performance' }));
      got.push(await (navigator as unknown as { gpu: typeof gpu }).gpu.requestAdapter());
      return 'engine';
    });
    expect(engine).toBe('engine');
    expect(got).toEqual(['probed adapter', 'fresh adapter']);
    expect(asked).toEqual([undefined]);
    expect(gpu.requestAdapter).toBe(own);
    vi.unstubAllGlobals();
  });

  it('puts the browser`s method back when the engine fails', async () => {
    const { gpu } = stubGpu();
    const own = gpu.requestAdapter;
    await expect(answeringAdapterRequest('probed adapter', () => Promise.reject(new Error('no device')))).rejects.toThrow('no device');
    expect(gpu.requestAdapter).toBe(own);
    vi.unstubAllGlobals();
  });

  it('is handed to the engine the host creates', async () => {
    const browser = stubGpu().gpu;
    const seen: unknown[] = [];
    const { module } = fakeModule();
    const create = module.WatercolourEngine.create;
    module.WatercolourEngine.create = async () => {
      seen.push(await browser.requestAdapter());
      return create();
    };
    let adapter: unknown = 'probed adapter';
    const host = new EngineHost({
      loadModule: async () => module,
      capabilities: gpu,
      probedAdapter: () => {
        const a = adapter;
        adapter = undefined;
        return a;
      },
    });
    await host.acquire();
    expect(seen).toEqual(['probed adapter']);
    vi.unstubAllGlobals();
  });
});
