import { type Capabilities, detectCapabilities, takeProbedAdapter } from './capabilities';
import { getPresentation } from './presentation';
import { WatercolourError, toWatercolourError } from './errors';
import type { EngineStats, WasmEngine, WasmModule } from './wasm-types';

export type { EngineStats, WasmEngine, WasmInstance, WasmModule } from './wasm-types';

export interface EngineLease {
  module: WasmModule;
  engine: WasmEngine;
}

export interface EngineHostOptions {
  /** Live instances the page may run at once; the rest fall back. */
  maxLiveInstances?: number;
  /** Test seam: supplies the bindings instead of importing `../wasm`. */
  loadModule?: () => Promise<WasmModule>;
  /** Test seam: the probe asked before the module is fetched. */
  capabilities?: () => Promise<Capabilities>;
  /** Test seam: the adapter the probe was handed, taken once. */
  probedAdapter?: () => unknown;
}

/** How long {@link EngineHost.warmWhenIdle} waits for an idle period before warming anyway. */
export const WARM_IDLE_TIMEOUT_MS = 3000;

export const DEFAULT_MAX_LIVE_INSTANCES = 4;

/**
 * A glob rather than a static import: `src/wasm/` is gitignored, and a
 * static `import('../wasm/…')` fails Vite's transform when the file is
 * absent. The glob is empty in that case and the host reports
 * `EngineUnavailable`, which the player answers with baked/static.
 */
const wasmGlue = import.meta.glob('../wasm/nocturne_watercolour.js') as Record<string, () => Promise<WasmModule>>;

let warnedMissing = false;

async function importBindings(): Promise<WasmModule> {
  const loader = Object.values(wasmGlue)[0];
  if (!loader) {
    if (import.meta.env.DEV && !warnedMissing) {
      warnedMissing = true;
      console.warn('[watercolour] wasm bindings not built (pnpm --filter @nocturne/watercolour build:wasm); using baked/static artwork');
    }
    throw new WatercolourError('EngineUnavailable', 'wasm bindings are not built');
  }
  const module = await loader();
  await module.default();
  return module;
}

/**
 * One WebGPU device per page. `release` never tears the engine down: its
 * compiled pipelines cost more to rebuild than to keep.
 */
export class EngineHost {
  private lease: Promise<EngineLease> | undefined;
  private module: WasmModule | undefined;
  private engine: WasmEngine | undefined;
  private leases = 0;
  private stillQueue: Promise<void> = Promise.resolve();
  private lostFlag = false;
  private readonly lostListeners = new Set<(message: string) => void>();
  private maxLive: number;
  private readonly loadModule: () => Promise<WasmModule>;
  private readonly capabilities: () => Promise<Capabilities>;
  private readonly probedAdapter: () => unknown;
  /** Marks for `performance.getEntriesByName`, so hosts can time first paint against init. */
  readonly marks = { moduleLoaded: 'watercolour:module-loaded', engineReady: 'watercolour:engine-ready' };

  constructor(options: EngineHostOptions = {}) {
    this.maxLive = options.maxLiveInstances ?? DEFAULT_MAX_LIVE_INSTANCES;
    this.loadModule = options.loadModule ?? importBindings;
    this.capabilities = options.capabilities ?? detectCapabilities;
    this.probedAdapter = options.probedAdapter ?? (options.capabilities ? () => undefined : takeProbedAdapter);
  }

  get lost(): boolean {
    return this.lostFlag;
  }

  get refCount(): number {
    return this.leases;
  }

  get maxLiveInstances(): number {
    return this.maxLive;
  }

  set maxLiveInstances(value: number) {
    this.maxLive = Math.max(1, Math.floor(value));
    if (this.engine) this.engine.maxLiveInstances = this.maxLive;
  }

  /** `true` once another live instance would be refused. */
  get capReached(): boolean {
    const stats = this.stats();
    return stats ? stats.liveInstances >= stats.maxLiveInstances : false;
  }

  get ready(): boolean {
    return this.engine !== undefined && !this.lostFlag;
  }

  acquire(): Promise<EngineLease> {
    if (this.lostFlag) {
      this.lease = undefined;
      this.engine = undefined;
      this.lostFlag = false;
    }
    this.lease ??= this.create().catch((error) => {
      // Not cached: a GPU process restart can make the next attempt succeed.
      this.lease = undefined;
      throw toWatercolourError(error);
    });
    this.leases += 1;
    return this.lease;
  }

  release(): void {
    this.leases = Math.max(0, this.leases - 1);
  }

  /**
   * Waits for the turn of a live instance that renders one frame and releases
   * (a still), and resolves with the function that ends it.
   *
   * Stills mount in bursts - a member list's avatars - and each resolves its
   * mode before any has taken a slot, so unqueued they claim the whole cap at
   * once and starve whatever else mounts beside them. One at a time they hold
   * a single slot for a few milliseconds each.
   */
  stillTurn(): Promise<() => void> {
    let end!: () => void;
    const turn = new Promise<void>((resolve) => (end = resolve));
    const ready = this.stillQueue.then(() => end);
    this.stillQueue = this.stillQueue.then(() => turn);
    return ready;
  }

  /**
   * Builds the device and its pipelines without holding a slot.
   *
   * The first `acquire` blocks the main thread while the wasm module loads and
   * WebGPU hands over a device, measured at 375 ms on a discrete laptop GPU.
   * Paid on a pointer-enter, it freezes the very transition it was starting.
   * A host that expects live artwork warms the engine at idle instead.
   *
   * Resolves `false` when there is no usable GPU. That is not an error: the
   * baked and static paths cover it, and they are what would have been chosen.
   * The adapter is asked for before the module is fetched, so a machine
   * without one downloads and compiles nothing.
   *
   * Does nothing while the presentation is `off`: nothing will be drawn.
   */
  async warm(): Promise<boolean> {
    if (getPresentation() === 'off') return false;
    try {
      await this.acquire();
      return true;
    } catch {
      return false;
    } finally {
      this.release();
    }
  }

  /**
   * {@link warm} in the browser's next idle period, or after `timeoutMs` if
   * the page never goes idle, so the boot does not compete with first paint.
   */
  warmWhenIdle(timeoutMs = WARM_IDLE_TIMEOUT_MS): Promise<boolean> {
    return new Promise<void>((resolve) => {
      if (typeof requestIdleCallback === 'function') requestIdleCallback(() => resolve(), { timeout: timeoutMs });
      else setTimeout(resolve, 0);
    }).then(() => this.warm());
  }

  onLost(listener: (message: string) => void): () => void {
    this.lostListeners.add(listener);
    return () => this.lostListeners.delete(listener);
  }

  stats(): EngineStats | undefined {
    if (!this.engine) return undefined;
    try {
      return this.engine.stats();
    } catch {
      return undefined;
    }
  }

  private async create(): Promise<EngineLease> {
    if (!this.module) {
      const capabilities = await this.capabilities();
      if (!capabilities.webgpu || !capabilities.adapter) {
        throw new WatercolourError('WebGpuUnavailable', capabilities.reason ?? 'no WebGPU adapter');
      }
      this.module = await this.loadModule();
      performance.mark?.(this.marks.moduleLoaded);
    }
    const module = this.module;
    const engine = await answeringAdapterRequest(this.probedAdapter(), () => module.WatercolourEngine.create());
    engine.maxLiveInstances = this.maxLive;
    engine.onDeviceLost((message: string) => this.handleLost(message));
    this.engine = engine;
    performance.mark?.(this.marks.engineReady);
    return { module: this.module, engine };
  }

  private handleLost(message: string): void {
    this.lostFlag = true;
    this.engine = undefined;
    this.lease = undefined;
    const error = new WatercolourError('DeviceLost', message);
    for (const listener of Array.from(this.lostListeners)) listener(error.message);
  }
}

/**
 * Runs `create` with `navigator.gpu.requestAdapter` answering its first call
 * with `adapter`: the engine asks WebGPU for an adapter of its own, and a
 * second request is another round trip to the GPU process for the one the
 * probe already holds. Only that first call is answered.
 */
export async function answeringAdapterRequest<T>(adapter: unknown, create: () => Promise<T>): Promise<T> {
  const gpu = typeof navigator === 'undefined' ? undefined : (navigator as { gpu?: Record<string, unknown> }).gpu;
  if (!adapter || !gpu) return create();
  const own = Object.prototype.hasOwnProperty.call(gpu, 'requestAdapter');
  const previous = gpu.requestAdapter;
  const restore = () => {
    if (own) gpu.requestAdapter = previous;
    else delete gpu.requestAdapter;
  };
  gpu.requestAdapter = () => {
    restore();
    return Promise.resolve(adapter);
  };
  try {
    return await create();
  } finally {
    restore();
  }
}

let shared: EngineHost | undefined;

export function getEngineHost(): EngineHost {
  shared ??= new EngineHost();
  return shared;
}

/** Replaces the page singleton; call before the first artwork mounts. */
export function configureEngineHost(options: EngineHostOptions): EngineHost {
  shared = new EngineHost(options);
  return shared;
}
