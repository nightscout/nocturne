import type { ArtworkOptions, DetailLevel, Surface } from '../types';
import { DEFAULT_DURATION_MS, DEFAULT_TAIL, detailForEdge } from '../types';
import { type AssetKey, type AssetOptions, assetAvailable, assetUrl, iconAssetKey, loadManifest } from './assets';
import { bakedServesEdge, type BakedManifest, type StripBitmap, drawStill, drawStripFrame, loadStrip, parseBakedManifest, sharedStill } from './baked';
import { type Capabilities, detectCapabilities } from './capabilities';
import { type EngineHost, type EngineLease, type WasmInstance, getEngineHost } from './engine-host';
import { WatercolourError, toWatercolourError } from './errors';
import { type ResolvedMode, fallbackOrder, resolveMode, resolveMotion } from './mode';
import { type ArtworkRef, type IconRef, type SceneSource, authoredSceneJson, iconSvg, isArtworkRef, isIconRef, parseSceneDocument, resolveSceneJson } from './scenes';
import { type Scheduler, type SchedulerHandle, getScheduler } from './scheduler';

export type PlayerEvent = 'ready' | 'finished' | 'fallback' | 'error' | 'statechange';

declare global {
  interface Window {
    /** DEV only: live wasm instances keyed by canvas, for the browser audit. */
    __watercolourLive?: Array<{ canvas: HTMLCanvasElement; instance: WasmInstance }>;
  }
}

export interface PlayerState {
  mode: 'pending' | ResolvedMode;
  motion: 'full' | 'reduced';
  playing: boolean;
  finished: boolean;
  /** Raw wall-clock fraction: linear elapsed, or the engine's own curve. */
  progress: number;
  /** The value fed to the simulation: the caller easing applied to `progress`. */
  easedProgress: number;
  error?: WatercolourError;
  /** Set when a backend failed and a lower one took over. */
  fallbackReason?: string;
  /** Live only: the detail tier and sim grid the loaded scene resolved to. */
  detail?: DetailLevel;
  simResolution?: number;
  /** Live only: simulation steps the loaded scene's timeline runs. */
  totalTicks?: number;
}

export interface FallbackDetail {
  from: ResolvedMode;
  error: WatercolourError;
}

export interface PlayerOptions extends ArtworkOptions, AssetOptions {
  surface?: Surface;
  /** Overrides the detail tier derived from the artwork reference (live). */
  detail?: DetailLevel;
  /** Live only: overrides the simulation grid side; 0 keeps the detail's default. */
  simResolution?: number;
  /** CSS size and device pixel ratio at creation; defaults to the canvas's current size. */
  width?: number;
  height?: number;
  dpr?: number;
  /**
   * For live mode: finish on first appearance, present one frame, then
   * dispose the engine instance while keeping the canvas pixels, so the
   * artwork holds no live slot and no checkpoints. Meant for stills that are
   * cheap to render but numerous (avatars).
   */
  releaseAfterFinish?: boolean;
  /**
   * Live only: GPU memory this instance may spend on seek checkpoints.
   * Absent keeps the engine's default; 0 leaves it with the single
   * checkpoint at tick 0, so a backwards seek replays from the start
   * instead of restoring a nearer state.
   */
  checkpointBudgetBytes?: number;
  scheduler?: Scheduler;
  engineHost?: EngineHost;
  capabilities?: () => Promise<Capabilities>;
}

export interface ArtworkPlayer {
  play(): void;
  pause(): void;
  /** Back to the blank sheet, paused. */
  reset(): void;
  /** Artistic progress 0..1; pauses. */
  seek(progress: number): void;
  finishImmediately(): void;
  resize(width: number, height: number, dpr?: number): void;
  dispose(): void;
  on(event: 'fallback', listener: (detail: FallbackDetail) => void): () => void;
  on(event: 'error', listener: (error: WatercolourError) => void): () => void;
  on(event: 'ready' | 'finished' | 'statechange', listener: () => void): () => void;
  readonly state: PlayerState;
  /** Resolves once a backend is drawing (or the player settled on `none`). */
  readonly ready: Promise<void>;
  /** The element being drawn to; differs from the one passed in only after a live-to-baked fallback. */
  readonly canvas: HTMLCanvasElement;
  /** Live mode only: PNG of the current state at `width` x `height`. */
  exportPng(width: number, height: number): Promise<Uint8Array>;
  /** Live mode only: the baked strip and its manifest; leaves the playback finished. */
  exportStrip(frames: number, size: number): Promise<{ strip: Uint8Array; manifest: BakedManifest }>;
}

interface BackendCallbacks {
  onFinished(): void;
  onFault(error: WatercolourError): void;
}

interface PixelSize {
  width: number;
  height: number;
}

interface Backend {
  readonly mode: ResolvedMode;
  readonly canvas: HTMLCanvasElement;
  readonly playing: boolean;
  readonly finished: boolean;
  readonly progress: number;
  /** The eased value fed to the simulation; `progress` is the raw wall fraction. */
  readonly easedProgress: number;
  readonly detail?: DetailLevel;
  readonly simResolution?: number;
  readonly totalTicks?: number;
  play(): void;
  pause(): void;
  reset(): void;
  seek(progress: number): void;
  finish(): void;
  resize(size: PixelSize): void;
  dispose(): void;
  exportPng?(width: number, height: number): Promise<Uint8Array>;
  exportStrip?(frames: number, size: number): Promise<Uint8Array>;
}

function pixelSize(width: number, height: number, dpr: number): PixelSize {
  const ratio = Math.min(2, Math.max(0.5, Number.isFinite(dpr) && dpr > 0 ? dpr : 1));
  return {
    width: Math.max(1, Math.round(width * ratio)),
    height: Math.max(1, Math.round(height * ratio)),
  };
}

/**
 * The swap between context types is one-way: a canvas that has handed out a
 * WebGPU context can never give a 2D one, and a 2D-locked canvas can never
 * give a WebGPU one. A backend that needs the other context therefore
 * replaces the element in place. `twoDUsed` records which canvases hold a 2D
 * context so a live backend knows to swap in a fresh element before attach.
 */
const twoDUsed = new WeakSet<HTMLCanvasElement>();

function acquire2d(canvas: HTMLCanvasElement): { canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D } {
  const ctx = canvas.getContext('2d');
  if (ctx) {
    twoDUsed.add(canvas);
    return { canvas, ctx };
  }
  const fresh = canvas.cloneNode(false) as HTMLCanvasElement;
  canvas.replaceWith(fresh);
  const freshCtx = fresh.getContext('2d');
  if (!freshCtx) throw new WatercolourError('Engine', 'no 2D canvas context available');
  twoDUsed.add(fresh);
  return { canvas: fresh, ctx: freshCtx };
}

/** The element for a WebGPU swapchain, replacing a 2D-locked one in place. */
function acquireWebgpu(canvas: HTMLCanvasElement): HTMLCanvasElement {
  if (!twoDUsed.has(canvas)) return canvas;
  const fresh = canvas.cloneNode(false) as HTMLCanvasElement;
  canvas.replaceWith(fresh);
  return fresh;
}

/** Numeric inverse of a monotonic easing, for seek-to-artistic-progress. */
export function invertEasing(easing: (t: number) => number, y: number): number {
  if (y <= 0) return 0;
  if (y >= 1) return 1;
  let lo = 0;
  let hi = 1;
  for (let i = 0; i < 24; i++) {
    const mid = (lo + hi) / 2;
    if (easing(mid) < y) lo = mid;
    else hi = mid;
  }
  return (lo + hi) / 2;
}

class LiveBackend implements Backend {
  readonly mode = 'live' as const;
  private dirty = true;
  private isPlaying = false;
  private handle: SchedulerHandle;
  private disposed = false;
  private released = false;
  private readonly releaseAfterFinish: boolean;
  private readonly unsubscribeLost: () => void;
  private readonly easing?: (t: number) => number;
  private readonly resolvedDetail: DetailLevel;
  private readonly durationMs: number;
  /** Wall-clock progress holder while `easing` drives `advanceToProgress`. */
  private elapsedMs = 0;

  static async create(
    canvas: HTMLCanvasElement,
    source: SceneSource,
    durationMs: number,
    size: PixelSize,
    host: EngineHost,
    scheduler: Scheduler,
    callbacks: BackendCallbacks,
    options: PlayerOptions,
  ): Promise<LiveBackend> {
    const lease = await host.acquire();
    try {
      // Live reveals pick the detail tier from the canvas's BACKING long edge
      // (the size passed in is DPR-scaled). The simulation grid is the tier's
      // own, whatever the canvas: the fluid moves in cells, so a different
      // grid paints a different picture, and one tier must paint the same one
      // at every size. An explicit `detail`/`simResolution` option wins.
      const longEdge = Math.max(size.width, size.height);
      const resolvedDetail = options.detail ?? detailForEdge(longEdge);
      const sceneJson = isArtworkRef(source) || isIconRef(source)
        ? resolveSceneJson(lease.module, source, {
            detail: resolvedDetail,
            simResolution: options.simResolution,
          })
        : authoredSceneJson(source, lease.module);
      parseSceneDocument(sceneJson);
      // Catalogue scenes carry their own tick tail; only the wall-clock split
      // is passed through, so `tail` is the share of the duration the paint
      // phase does NOT get.
      const instance = lease.engine.createInstance(
        sceneJson,
        durationMs,
        0,
        1 - (options.tail ?? DEFAULT_TAIL),
        options.checkpointBudgetBytes,
      );
      if (options.easing) instance.setProgressCurve('linear');
      try {
        const target = acquireWebgpu(canvas);
        instance.attach(target, size.width, size.height);
        if (import.meta.env.DEV) (window.__watercolourLive ??= []).push({ canvas: target, instance });
        return new LiveBackend(
          target,
          instance,
          lease,
          host,
          scheduler,
          callbacks,
          options.releaseAfterFinish ?? false,
          options.easing,
          resolvedDetail,
          durationMs,
        );
      } catch (error) {
        instance.dispose();
        throw error;
      }
    } catch (error) {
      host.release();
      throw toWatercolourError(error);
    }
  }

  private constructor(
    readonly canvas: HTMLCanvasElement,
    private readonly instance: WasmInstance,
    private readonly lease: EngineLease,
    private readonly host: EngineHost,
    scheduler: Scheduler,
    private readonly callbacks: BackendCallbacks,
    releaseAfterFinish: boolean,
    easing: ((t: number) => number) | undefined,
    resolvedDetail: DetailLevel,
    durationMs: number,
  ) {
    this.releaseAfterFinish = releaseAfterFinish;
    this.easing = easing;
    this.resolvedDetail = resolvedDetail;
    this.durationMs = durationMs;
    this.handle = scheduler.register({
      element: canvas,
      tick: (dt) => this.tick(dt),
      render: () => this.render(),
    });
    this.unsubscribeLost = host.onLost((message) => this.fault(new WatercolourError('DeviceLost', message)));
    this.handle.setActive(true);
  }

  get playing(): boolean {
    return this.isPlaying;
  }

  get finished(): boolean {
    if (this.released) return true;
    return this.guarded(() => this.instance.isFinished(), false);
  }

  get progress(): number {
    if (this.released) return 1;
    if (this.easing) return Math.min(1, this.elapsedMs / this.durationMs);
    return this.guarded(() => this.instance.progress(), 0);
  }

  get easedProgress(): number {
    if (this.released) return 1;
    if (this.easing) return this.easing(Math.min(1, this.elapsedMs / this.durationMs));
    return this.guarded(() => this.instance.progress(), 0);
  }

  get detail(): DetailLevel {
    return this.resolvedDetail;
  }

  get simResolution(): number | undefined {
    if (this.released) return undefined;
    return this.guarded(() => this.instance.simResolution(), undefined);
  }

  get totalTicks(): number | undefined {
    if (this.released) return undefined;
    return this.guarded(() => this.instance.totalTicks(), undefined);
  }

  play(): void {
    if (this.disposed || this.released || this.finished) return;
    this.instance.play();
    this.isPlaying = true;
    this.handle.setActive(true);
  }

  pause(): void {
    if (this.disposed || this.released) return;
    this.instance.pause();
    this.isPlaying = false;
  }

  reset(): void {
    if (this.easing) this.elapsedMs = 0;
    this.step(() => this.instance.reset());
    this.isPlaying = false;
  }

  seek(progress: number): void {
    const p = Math.min(1, Math.max(0, progress));
    if (this.easing) {
      // `seek` takes the artistic (eased) progress the caller sees, so drive
      // the engine to `p` directly and park the wall clock at its inverse.
      this.elapsedMs = invertEasing(this.easing, p) * this.durationMs;
      this.step(() => this.instance.seekProgress(p));
    } else {
      this.step(() => this.instance.seekProgress(p));
    }
    this.isPlaying = this.guarded(() => this.instance.isPlaying(), false);
  }

  finish(): void {
    if (this.disposed || this.released) return;
    if (this.easing) this.elapsedMs = this.durationMs;
    this.step(() => this.instance.finishImmediately());
    this.isPlaying = false;
    this.callbacks.onFinished();
  }

  resize(size: PixelSize): void {
    this.step(() => this.instance.resize(size.width, size.height));
  }

  async exportPng(width: number, height: number): Promise<Uint8Array> {
    this.ensureAlive();
    try {
      return await this.instance.exportPng(width, height);
    } catch (error) {
      throw toWatercolourError(error);
    }
  }

  async exportStrip(frames: number, size: number): Promise<Uint8Array> {
    this.ensureAlive();
    try {
      const strip = await this.instance.exportStrip(frames, size);
      this.isPlaying = false;
      this.dirty = true;
      this.handle.setActive(true);
      return strip;
    } catch (error) {
      throw toWatercolourError(error);
    }
  }

  exportManifest(frames: number, size: number, durationMs: number): BakedManifest {
    return parseBakedManifest(this.lease.module.bakedManifest(frames, size, size, durationMs));
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    if (!this.released) {
      this.unsubscribeLost();
      this.handle.dispose();
      try {
        this.instance.dispose();
      } catch {
        // Already freed by a device loss; nothing left to release.
      }
      this.host.release();
    }
  }

  private tick(dt: number): void {
    if (!this.isPlaying || this.disposed || this.released) return;
    if (this.easing) {
      this.elapsedMs = Math.min(this.durationMs, this.elapsedMs + dt * 1000);
      this.step(() => this.instance.advanceToProgress(this.easing!(this.elapsedMs / this.durationMs)));
    } else {
      this.step(() => this.instance.advanceByElapsed(dt));
    }
  }

  private render(): void {
    if (this.disposed || this.released) return;
    if (this.dirty) {
      this.dirty = false;
      try {
        this.instance.render();
      } catch (error) {
        this.fault(toWatercolourError(error));
        return;
      }
      if (this.isPlaying && this.instance.isFinished()) {
        this.isPlaying = false;
        this.callbacks.onFinished();
      }
      if (this.releaseAfterFinish && this.guarded(() => this.instance.isFinished(), true)) {
        // The canvas keeps the presented frame; free the slot and checkpoints.
        this.releaseResources();
        return;
      }
    }
    if (!this.isPlaying && !this.dirty && !this.released) this.handle.setActive(false);
  }

  private releaseResources(): void {
    if (this.released || this.disposed) return;
    this.released = true;
    this.unsubscribeLost();
    this.handle.dispose();
    try {
      this.instance.dispose();
    } catch {
      // Already freed by a device loss; nothing left to release.
    }
    this.host.release();
  }

  private step(action: () => void): void {
    if (this.disposed || this.released) return;
    try {
      action();
      this.dirty = true;
      this.handle.setActive(true);
    } catch (error) {
      this.fault(toWatercolourError(error));
    }
  }

  private guarded<T>(read: () => T, fallback: T): T {
    if (this.disposed) return fallback;
    try {
      return read();
    } catch {
      return fallback;
    }
  }

  private ensureAlive(): void {
    if (this.disposed || this.released) throw new WatercolourError('Disposed', 'player disposed');
  }

  private fault(error: WatercolourError): void {
    if (this.disposed || this.released) return;
    this.isPlaying = false;
    this.callbacks.onFault(error);
  }
}

class BakedBackend implements Backend {
  readonly mode = 'baked' as const;
  private elapsedMs = 0;
  private isPlaying = false;
  private dirty = true;
  private size: PixelSize;
  private readonly handle: SchedulerHandle;
  private disposed = false;
  private readonly easing?: (t: number) => number;
  private readonly durationMs: number;

  static async create(
    canvas: HTMLCanvasElement,
    urls: { manifest: string; strip: string },
    durationMs: number,
    size: PixelSize,
    scheduler: Scheduler,
    callbacks: BackendCallbacks,
    options: PlayerOptions,
  ): Promise<BakedBackend> {
    const manifest = await loadManifest(urls.manifest);
    const strip = await loadStrip(urls.strip, manifest);
    const target = acquire2d(canvas);
    return new BakedBackend(target.canvas, target.ctx, strip, durationMs, size, scheduler, callbacks, options.easing);
  }

  private constructor(
    readonly canvas: HTMLCanvasElement,
    private readonly ctx: CanvasRenderingContext2D,
    private readonly strip: StripBitmap,
    durationMs: number,
    size: PixelSize,
    scheduler: Scheduler,
    private readonly callbacks: BackendCallbacks,
    easing: ((t: number) => number) | undefined,
  ) {
    this.durationMs = durationMs;
    this.easing = easing;
    this.size = size;
    this.applySize();
    this.handle = scheduler.register({
      element: canvas,
      tick: (dt) => this.tick(dt),
      render: () => this.render(),
    });
    this.handle.setActive(true);
  }

  get playing(): boolean {
    return this.isPlaying;
  }

  get progress(): number {
    return Math.min(1, this.elapsedMs / this.durationMs);
  }

  /** The eased value actually drawn from the strip; strips are baked at wall-clock spacing, so no tail-hold. */
  get easedProgress(): number {
    return this.frameProgress();
  }

  get finished(): boolean {
    return this.progress >= 1;
  }

  play(): void {
    if (this.disposed || this.finished) return;
    this.isPlaying = true;
    this.handle.setActive(true);
  }

  pause(): void {
    this.isPlaying = false;
  }

  reset(): void {
    this.elapsedMs = 0;
    this.isPlaying = false;
    this.invalidate();
  }

  seek(progress: number): void {
    this.elapsedMs = Math.min(1, Math.max(0, progress)) * this.durationMs;
    this.isPlaying = false;
    this.invalidate();
  }

  finish(): void {
    this.elapsedMs = this.durationMs;
    this.isPlaying = false;
    this.invalidate();
    this.callbacks.onFinished();
  }

  resize(size: PixelSize): void {
    this.size = size;
    this.applySize();
    this.invalidate();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.handle.dispose();
    this.strip.bitmap.close();
  }

  private applySize(): void {
    this.canvas.width = this.size.width;
    this.canvas.height = this.size.height;
  }

  private invalidate(): void {
    if (this.disposed) return;
    this.dirty = true;
    this.handle.setActive(true);
  }

  private tick(dt: number): void {
    if (!this.isPlaying) return;
    this.elapsedMs = Math.min(this.durationMs, this.elapsedMs + dt * 1000);
    this.dirty = true;
  }

  /** Progress fed to the strip: eased, linear over the wall clock. */
  private frameProgress(): number {
    const t = Math.min(1, this.elapsedMs / this.durationMs);
    return this.easing ? this.easing(t) : t;
  }

  private render(): void {
    if (this.disposed) return;
    if (this.dirty) {
      this.dirty = false;
      drawStripFrame(this.ctx, this.strip, this.frameProgress(), this.size.width, this.size.height);
      if (this.isPlaying && this.finished) {
        this.isPlaying = false;
        this.callbacks.onFinished();
      }
    }
    if (!this.isPlaying && !this.dirty) this.handle.setActive(false);
  }
}

class StaticBackend implements Backend {
  readonly mode = 'static' as const;
  readonly playing = false;
  readonly finished = true;
  readonly progress = 1;
  readonly easedProgress = 1;
  private disposed = false;

  static async create(canvas: HTMLCanvasElement, url: string, size: PixelSize, callbacks: BackendCallbacks): Promise<StaticBackend> {
    const image = await sharedStill(url);
    const target = acquire2d(canvas);
    const backend = new StaticBackend(target.canvas, target.ctx, image, size);
    queueMicrotask(() => callbacks.onFinished());
    return backend;
  }

  private constructor(
    readonly canvas: HTMLCanvasElement,
    private readonly ctx: CanvasRenderingContext2D,
    private readonly image: ImageBitmap,
    private size: PixelSize,
  ) {
    this.draw();
  }

  play(): void {}
  pause(): void {}
  reset(): void {}
  seek(): void {}
  finish(): void {}

  resize(size: PixelSize): void {
    this.size = size;
    this.draw();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    // The bitmap is borrowed from `sharedStill` and outlives this backend.
  }

  private draw(): void {
    if (this.disposed) return;
    this.canvas.width = this.size.width;
    this.canvas.height = this.size.height;
    drawStill(this.ctx, this.image, this.size.width, this.size.height);
  }
}

/**
 * The plain-Lucide-SVG fallback for icon sources: draws the element list as
 * an inline SVG image when no GPU is available. It always resolves, so an
 * icon never falls to `none`.
 */
class IconSvgBackend implements Backend {
  readonly mode = 'static' as const;
  readonly playing = false;
  readonly finished = true;
  readonly progress = 1;
  readonly easedProgress = 1;
  private disposed = false;

  static async create(
    canvas: HTMLCanvasElement,
    ref: IconRef,
    size: PixelSize,
    callbacks: BackendCallbacks,
  ): Promise<IconSvgBackend> {
    const target = acquire2d(canvas);
    const backend = new IconSvgBackend(target.canvas, target.ctx, ref, size);
    await backend.draw();
    if (!backend.disposed) queueMicrotask(() => callbacks.onFinished());
    return backend;
  }

  private constructor(
    readonly canvas: HTMLCanvasElement,
    private readonly ctx: CanvasRenderingContext2D,
    private readonly ref: IconRef,
    private size: PixelSize,
  ) {}

  play(): void {}
  pause(): void {}
  reset(): void {}
  seek(): void {}
  finish(): void {}

  resize(size: PixelSize): void {
    this.size = size;
    void this.draw();
  }

  dispose(): void {
    this.disposed = true;
  }

  private async draw(): Promise<void> {
    if (this.disposed) return;
    this.canvas.width = this.size.width;
    this.canvas.height = this.size.height;
    const url = URL.createObjectURL(
      new Blob([iconSvg(this.ref.icon, this.ref.surface ?? 'light')], { type: 'image/svg+xml;charset=utf-8' }),
    );
    try {
      const img = new Image();
      await new Promise<void>((resolve, reject) => {
        img.onload = () => resolve();
        img.onerror = () => reject(new WatercolourError('Engine', 'icon SVG decode failed'));
        img.src = url;
      });
      if (this.disposed) return;
      this.ctx.drawImage(img, 0, 0, this.size.width, this.size.height);
    } finally {
      URL.revokeObjectURL(url);
    }
  }
}

/**
 * The static rung an icon source draws: its baked final when one exists, else
 * the plain-SVG fallback, which always resolves so an icon never falls to
 * `none`. Exported so the two paths are unit-testable without a canvas.
 */
export function iconStaticBackend(icon: IconRef | undefined, hasFinal: boolean): 'baked' | 'svg' {
  return icon && !hasFinal ? 'svg' : 'baked';
}

type Listener = (payload?: unknown) => void;

class Player implements ArtworkPlayer {
  private backend: Backend | undefined;
  private disposed = false;
  private size: PixelSize;
  private mode: 'pending' | ResolvedMode = 'pending';
  private motion: 'full' | 'reduced' = 'full';
  private error: WatercolourError | undefined;
  private fallbackReason: string | undefined;
  private currentCanvas: HTMLCanvasElement;
  private readonly listeners = new Map<PlayerEvent, Set<Listener>>();
  private readonly durationMs: number;
  private readonly host: EngineHost;
  private readonly scheduler: Scheduler;
  private readonly source: SceneSource;
  private readonly ref: ArtworkRef | undefined;
  private readonly icon: IconRef | undefined;
  readonly ready: Promise<void>;

  constructor(
    canvas: HTMLCanvasElement,
    source: SceneSource | string,
    private readonly options: PlayerOptions,
  ) {
    this.currentCanvas = canvas;
    this.source = typeof source === 'string' ? { sceneJson: source } : source;
    this.ref = isArtworkRef(this.source) ? this.source : undefined;
    this.icon = isIconRef(this.source) ? this.source : undefined;
    this.durationMs = options.durationMs && options.durationMs > 0 ? options.durationMs : DEFAULT_DURATION_MS;
    this.host = options.engineHost ?? getEngineHost();
    this.scheduler = options.scheduler ?? getScheduler();
    this.size = pixelSize(
      options.width ?? canvas.clientWidth ?? canvas.width,
      options.height ?? canvas.clientHeight ?? canvas.height,
      options.dpr ?? (typeof devicePixelRatio === 'number' ? devicePixelRatio : 1),
    );
    this.ready = this.init().catch((error) => {
      this.fail(toWatercolourError(error));
    });
  }

  get canvas(): HTMLCanvasElement {
    return this.currentCanvas;
  }

  get state(): PlayerState {
    const b = this.backend;
    return {
      mode: this.mode,
      motion: this.motion,
      playing: b?.playing ?? false,
      finished: b?.finished ?? false,
      progress: b?.progress ?? 0,
      easedProgress: b?.easedProgress ?? 0,
      error: this.error,
      fallbackReason: this.fallbackReason,
      detail: b?.detail,
      simResolution: b?.simResolution,
      totalTicks: b?.totalTicks,
    };
  }

  on(event: PlayerEvent, listener: (payload: never) => void): () => void {
    let set = this.listeners.get(event);
    if (!set) {
      set = new Set();
      this.listeners.set(event, set);
    }
    set.add(listener as Listener);
    return () => set?.delete(listener as Listener);
  }

  play(): void {
    this.backend?.play();
    this.emit('statechange');
  }

  pause(): void {
    this.backend?.pause();
    this.emit('statechange');
  }

  reset(): void {
    this.backend?.reset();
    this.emit('statechange');
  }

  seek(progress: number): void {
    this.backend?.seek(progress);
    this.emit('statechange');
  }

  finishImmediately(): void {
    this.backend?.finish();
    this.emit('statechange');
  }

  resize(width: number, height: number, dpr?: number): void {
    this.size = pixelSize(width, height, dpr ?? (typeof devicePixelRatio === 'number' ? devicePixelRatio : 1));
    this.backend?.resize(this.size);
  }

  async exportPng(width: number, height: number): Promise<Uint8Array> {
    await this.ready;
    if (!this.backend?.exportPng) throw new WatercolourError('Engine', `export requires live mode (current: ${this.mode})`);
    return this.backend.exportPng(width, height);
  }

  async exportStrip(frames: number, size: number): Promise<{ strip: Uint8Array; manifest: BakedManifest }> {
    await this.ready;
    const backend = this.backend;
    if (!(backend instanceof LiveBackend)) {
      throw new WatercolourError('Engine', `export requires live mode (current: ${this.mode})`);
    }
    const strip = await backend.exportStrip(frames, size);
    const manifest = backend.exportManifest(frames, size, this.durationMs);
    this.emit('statechange');
    return { strip, manifest };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.backend?.dispose();
    this.backend = undefined;
    this.listeners.clear();
  }

  private async init(): Promise<void> {
    const capabilities = await (this.options.capabilities ?? detectCapabilities)();
    if (this.disposed) return;
    this.motion = resolveMotion(this.options.motion ?? 'auto', capabilities.reducedMotion);
    // A baked icon source resolves to its `lucide-<name>` set like any
    // artwork; an unbaked one has no assets, so its plain-SVG fallback is the
    // guaranteed last rung below `live`.
    const hasBaked = this.bakedServesSize() && this.assetOk('strip') && this.assetOk('manifest');
    const hasStatic = this.icon ? true : this.assetOk('final');
    const first = resolveMode({
      requested: this.options.mode ?? 'auto',
      motion: this.options.motion ?? 'auto',
      capabilities,
      capReached: this.host.capReached,
      hasBaked,
      hasStatic,
      releaseAfterFinish: this.options.releaseAfterFinish,
    });
    if (first !== 'live') {
      // An explicit `live` request that resolveMode downgraded (usually the
      // instance cap) is a real fallback: report why instead of silently
      // drawing the baked asset.
      if (this.options.mode === 'live') {
        const capReached = this.host.capReached;
        const gpuOk = capabilities.webgpu && capabilities.adapter;
        const error = new WatercolourError(
          capReached ? 'InstanceLimit' : gpuOk ? 'AssetMissing' : 'WebGpuUnavailable',
          capReached
            ? `live unavailable: ${this.host.maxLiveInstances} live instances already (maxLiveInstances)`
            : gpuOk
              ? 'live unavailable: no baked asset at this resolution'
              : 'live unavailable: WebGPU is not available',
        );
        this.fallbackReason = `live: ${error.code}: ${error.message}`;
        this.emit('fallback', { from: 'live', error } satisfies FallbackDetail);
      }
      if (import.meta.env.DEV && !(this.options.mode !== undefined && this.options.mode !== 'auto' && first === this.options.mode)) {
        const reason = !capabilities.webgpu || !capabilities.adapter
          ? 'no WebGPU adapter'
          : this.host.capReached
            ? `instance cap reached (${this.host.maxLiveInstances})`
            : this.motion === 'reduced'
              ? 'reduced motion'
              : 'no assets';
        console.warn(`[watercolour] ${this.assetKey()?.id ?? 'player'} resolved ${this.options.mode ?? 'auto'} -> ${first} (${reason})`);
      }
    }
    await this.start([first, ...fallbackOrder(first, { hasBaked, hasStatic })]);
  }

  private assetKey(): AssetKey | undefined {
    if (this.ref) return this.ref;
    if (this.icon) return iconAssetKey(this.icon.name, this.icon.palette, this.icon.surface);
    return undefined;
  }

  /** Withholding baked at hero sizes lets `resolveMode` pick static instead. */
  private bakedServesSize(): boolean {
    return bakedServesEdge(Math.max(this.size.width, this.size.height));
  }

  private assetOk(variant: 'strip' | 'manifest' | 'final'): boolean {
    const key = this.assetKey();
    if (key) return assetAvailable(key, variant, this.options);
    return Boolean(this.options.assets?.[variant]);
  }

  private async start(chain: ResolvedMode[]): Promise<void> {
    let lastError: WatercolourError | undefined;
    for (const mode of chain) {
      if (this.disposed) return;
      if (mode === 'none') break;
      try {
        this.backend = await this.createBackend(mode);
        if (this.disposed) {
          this.backend.dispose();
          this.backend = undefined;
          return;
        }
        this.currentCanvas = this.backend.canvas;
        this.mode = mode;
        this.error = undefined;
        this.emit('ready');
        this.autoplay();
        this.emit('statechange');
        return;
      } catch (error) {
        lastError = toWatercolourError(error);
        this.fallbackReason = `${mode}: ${lastError.code}: ${lastError.message}`;
        if (import.meta.env.DEV) console.warn(`[watercolour] ${this.assetKey()?.id ?? 'player'} fell back ${mode} -> ${lastError.code}: ${lastError.message}`);
        this.emit('fallback', { from: mode, error: lastError } satisfies FallbackDetail);
      }
    }
    this.mode = 'none';
    if (lastError) this.fail(lastError);
    else this.emit('statechange');
  }

  private autoplay(): void {
    if (!this.backend) return;
    if (this.motion === 'reduced' || this.options.releaseAfterFinish) {
      this.backend.finish();
      return;
    }
    if ((this.options.autoplay ?? 'once') === 'once') this.backend.play();
  }

  private createBackend(mode: ResolvedMode): Promise<Backend> {
    const callbacks: BackendCallbacks = {
      onFinished: () => this.emit('finished'),
      onFault: (error) => this.handleFault(error),
    };
    switch (mode) {
      case 'live':
        return LiveBackend.create(
          this.currentCanvas,
          this.source,
          this.durationMs,
          this.size,
          this.host,
          this.scheduler,
          callbacks,
          this.options,
        );
      case 'baked':
        return this.resolveUrls(['manifest', 'strip']).then(([manifest, strip]) =>
          BakedBackend.create(this.currentCanvas, { manifest, strip }, this.durationMs, this.size, this.scheduler, callbacks, this.options),
        );
      case 'static':
        if (iconStaticBackend(this.icon, this.assetOk('final')) === 'svg') {
          return IconSvgBackend.create(this.currentCanvas, this.icon!, this.size, callbacks);
        }
        return this.resolveUrls(['final']).then(([final]) => StaticBackend.create(this.currentCanvas, final, this.size, callbacks));
      case 'none':
        return Promise.reject(new WatercolourError('AssetMissing', 'no asset available for this artwork'));
    }
  }

  private async resolveUrls<const V extends readonly ('manifest' | 'strip' | 'final')[]>(variants: V): Promise<string[]> {
    return Promise.all(
      variants.map(async (variant) => {
        const key = this.assetKey();
        const url = key ? await assetUrl(key, variant, this.options) : this.options.assets?.[variant];
        if (!url) throw new WatercolourError('AssetMissing', `no ${variant} asset for ${key?.id ?? 'scene document'}`);
        return url;
      }),
    );
  }

  /** A backend died underneath us (device lost, render error); move down the chain without live. */
  private handleFault(error: WatercolourError): void {
    if (this.disposed || !this.backend) return;
    const from = this.backend.mode;
    this.backend.dispose();
    this.backend = undefined;
    this.fallbackReason = `${from}: ${error.code}: ${error.message}`;
    this.emit('fallback', { from, error } satisfies FallbackDetail);
    const hasBaked = this.assetOk('strip') && this.assetOk('manifest');
    const hasStatic = this.assetOk('final');
    void this.start(fallbackOrder(from, { hasBaked, hasStatic }));
  }

  private fail(error: WatercolourError): void {
    this.error = error;
    this.emit('error', error);
    this.emit('statechange');
  }

  private emit(event: PlayerEvent, payload?: unknown): void {
    const set = this.listeners.get(event);
    if (!set) return;
    for (const listener of Array.from(set)) listener(payload);
  }
}

export function createArtworkPlayer(
  canvas: HTMLCanvasElement,
  source: SceneSource | string,
  options: PlayerOptions = {},
): ArtworkPlayer {
  return new Player(canvas, source, options);
}
