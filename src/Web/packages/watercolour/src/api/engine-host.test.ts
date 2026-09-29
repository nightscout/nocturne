import { describe, expect, it, vi } from 'vitest';
import { EngineHost } from './engine-host';
import type { WasmEngine, WasmModule } from './wasm-types';

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
    const host = new EngineHost({ loadModule: async () => module });

    expect(await host.warm()).toBe(true);
    expect(host.ready).toBe(true);
    // A warm-up that kept its lease would spend one of the four live slots
    // on nothing, and the fourth real mark would fall back to a still.
    expect(host.refCount).toBe(0);
    expect(created()).toBe(1);
  });

  it('does not build a second engine for the first real mark', async () => {
    const { module, created } = fakeModule();
    const host = new EngineHost({ loadModule: async () => module });
    await host.warm();
    await host.acquire();
    expect(created()).toBe(1);
    expect(host.refCount).toBe(1);
  });

  it('reports no GPU rather than throwing, because the still covers it', async () => {
    const host = new EngineHost({ loadModule: () => Promise.reject(new Error('no adapter')) });
    expect(await host.warm()).toBe(false);
    expect(host.ready).toBe(false);
    expect(host.refCount).toBe(0);
  });

  it('leaves a failed warm-up retryable', async () => {
    const loadModule = vi
      .fn<() => Promise<WasmModule>>()
      .mockRejectedValueOnce(new Error('no adapter'))
      .mockImplementation(async () => fakeModule().module);
    const host = new EngineHost({ loadModule });

    expect(await host.warm()).toBe(false);
    expect(await host.warm()).toBe(true);
    expect(loadModule).toHaveBeenCalledTimes(2);
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
