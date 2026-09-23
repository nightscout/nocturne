import {
  type ArtworkId,
  type ArtworkMode,
  type ArtworkPlayer,
  type ArtworkQuality,
  type BakedManifest,
  type DetailLevel,
  type EngineStats,
  type FrameStats,
  type PaletteId,
  type PlayerState,
  type Surface,
  type WatercolourError,
  PALETTE_IDS,
  artworkAspect,
  catalogueIds,
  createArtworkPlayer,
  detectCapabilities,
  getEngineHost,
  getScheduler,
} from '@nocturne/watercolour';
import { cubicOut, expoOut, linear, quadOut, quartOut, sineInOut } from 'svelte/easing';
import { mode } from 'mode-watcher';
import { LUCIDE_ICONS } from './lucide-icons';

export type PlaygroundArtwork = string;

/** `auto` follows the page theme (mode-watcher's `.dark` class on `<html>`). */
export type SurfaceMode = 'auto' | Surface;

export type EasingName = 'engine' | 'linear' | 'quadOut' | 'cubicOut' | 'quartOut' | 'expoOut' | 'sineInOut';

export const EASING_OPTIONS: readonly { value: EasingName; label: string }[] = [
  { value: 'engine', label: 'Engine default' },
  { value: 'linear', label: 'Linear' },
  { value: 'quadOut', label: 'Quad out' },
  { value: 'cubicOut', label: 'Cubic out' },
  { value: 'quartOut', label: 'Quart out' },
  { value: 'expoOut', label: 'Expo out' },
  { value: 'sineInOut', label: 'Sine in-out' },
];

const EASING_FNS: Record<Exclude<EasingName, 'engine'>, (t: number) => number> = {
  linear,
  quadOut,
  cubicOut,
  quartOut,
  expoOut,
  sineInOut,
};

export const DETAIL_OPTIONS: readonly { value: 'auto' | DetailLevel; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'small', label: 'Small' },
  { value: 'medium', label: 'Medium' },
  { value: 'large', label: 'Large' },
  { value: 'extraLarge', label: 'Extra large' },
];

export const OUTPUT_SIZES = [256, 512, 768, 1024] as const;
export type OutputSize = (typeof OUTPUT_SIZES)[number];

/** Simulation grid override; 0 = derive from the backing edge. */
export const SIM_OVERRIDES = [0, 96, 128, 160, 192, 256, 320, 384, 448, 512] as const;

export const PLAYGROUND_PALETTES: readonly { value: PaletteId; label: string }[] = PALETTE_IDS.map((p) => ({
  value: p,
  label: p.charAt(0).toUpperCase() + p.slice(1),
}));

export const PLAYGROUND_MODES: readonly { value: ArtworkMode; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'live', label: 'Live (WebGPU)' },
  { value: 'baked', label: 'Baked strip' },
  { value: 'static', label: 'Static PNG' },
];

export const STRIP_FRAMES = 12;
export const STRIP_EDGE = 256;
/** Stats refresh interval; the panel is deliberately not updated per frame. */
export const STATS_INTERVAL_MS = 250;
export const MAX_COMPONENT_DPR = 2;

export interface PlaygroundStats {
  engine?: EngineStats;
  frames: FrameStats;
  /** `performance.now()` at first presented frame minus navigation start, if measured. */
  firstPaintMs?: number;
  gpuInitMs?: number;
}

declare global {
  interface Window {
    __watercolour?: {
      firstPaintMs?: number;
      gpuInitMs?: number;
      marks: () => PerformanceEntry[];
    };
  }
}

/**
 * Bridges the framework-free player to Svelte state. The player never writes
 * reactive state per frame; `progress` is polled with the stats timer and on
 * the player's own events.
 */
export class PlaygroundState {
  artwork = $state<PlaygroundArtwork>('wash');
  availableArtworks = $state<string[]>(['wash', 'crescent-moon']);
  palette = $state<PaletteId>('water');
  /** `auto` follows the theme toggle; toggling dark mode switches the scene to the luminous variant. */
  surfaceMode = $state<SurfaceMode>('auto');
  surface = $derived<Surface>(this.surfaceMode === 'auto' ? (mode.current === 'dark' ? 'dark' : 'light') : this.surfaceMode);
  seed = $state(1610);
  intensity = $state(0.7);
  durationMs = $state(3000);
  mode = $state<ArtworkMode>('auto');
  detailMode = $state<'auto' | DetailLevel>('auto');
  /** 0 = auto; anything else overrides the simulation grid side. */
  simOverride = $state(0);
  outputSize = $state<OutputSize>(512);
  /**
   * The canvas in CSS px, at the artwork's own aspect with `outputSize` on the
   * long edge. A scene is authored at its aspect; simulated on a square it is
   * stretched.
   */
  readonly canvasSize = $derived.by(() => {
    const aspect = this.artwork.startsWith('lucide:') ? 1 : artworkAspect(this.artwork as ArtworkId);
    return aspect >= 1
      ? { width: this.outputSize, height: Math.round(this.outputSize / aspect) }
      : { width: Math.round(this.outputSize * aspect), height: this.outputSize };
  });
  easing = $state<EasingName>('engine');
  tail = $state(0.8);
  quality = $state<ArtworkQuality>('auto');
  dpr = $derived(Math.min(MAX_COMPONENT_DPR, typeof devicePixelRatio === 'number' ? devicePixelRatio : 1));

  playing = $state(false);
  finished = $state(false);
  progress = $state(0);
  resolvedMode = $state<PlayerState['mode']>('pending');
  fallbackReason = $state<string | undefined>(undefined);
  error = $state<WatercolourError | undefined>(undefined);
  webgpuAvailable = $state<boolean | undefined>(undefined);
  capabilityReason = $state<string | undefined>(undefined);
  stats = $state<PlaygroundStats>({ frames: { frames: 0, averageMs: 0, p95Ms: 0, lastMs: 0 } });
  exporting = $state(false);

  /** Live readouts: the effective backing edge, detail tier, sim grid and ticks. */
  resolvedDetail = $state<DetailLevel | undefined>(undefined);
  simResolution = $state<number | undefined>(undefined);
  totalTicks = $state<number | undefined>(undefined);
  /** Engine-reported adapter name, or a `GPUAdapterInfo` fallback when the wasm is empty. */
  adapter = $state<string | undefined>(undefined);

  private player: ArtworkPlayer | undefined;
  private canvas: HTMLCanvasElement | undefined;
  private statsTimer: ReturnType<typeof setInterval> | undefined;
  private scrubbing = false;
  private adapterPromise: Promise<string> | undefined;

  /** Recreated whenever a scene-defining control changes. */
  readonly sceneKey = $derived(
    `${this.artwork}|${this.palette}|${this.surface}|${this.seed}|${this.intensity}|${this.durationMs}|${this.mode}|${this.detailMode}|${this.simOverride}|${this.outputSize}|${this.dpr}|${this.easing}|${this.tail}|${this.quality}`,
  );

  async attach(canvas: HTMLCanvasElement): Promise<void> {
    this.canvas = canvas;
    void catalogueIds().then((ids) => {
      if (ids.length) this.availableArtworks = ids;
    });
    const caps = await detectCapabilities();
    this.webgpuAvailable = caps.webgpu && caps.adapter;
    this.capabilityReason = caps.reason;
    this.statsTimer = setInterval(() => this.refreshStats(), STATS_INTERVAL_MS);
    this.recreate();
  }

  detach(): void {
    if (this.statsTimer) clearInterval(this.statsTimer);
    this.statsTimer = undefined;
    this.player?.dispose();
    this.player = undefined;
    this.canvas = undefined;
  }

  recreate(): void {
    const canvas = this.canvas;
    if (!canvas) return;
    this.player?.dispose();
    this.resolvedMode = 'pending';
    this.fallbackReason = undefined;
    this.error = undefined;
    this.playing = false;
    this.finished = false;
    this.progress = 0;
    const icon = this.artwork.startsWith('lucide:') ? LUCIDE_ICONS.find((i) => i.id === this.artwork) : undefined;
    const detail = this.detailMode === 'auto' ? {} : { detail: this.detailMode };
    const player = createArtworkPlayer(
      canvas,
      icon
        ? { icon: icon.icon, name: icon.name, palette: this.palette, surface: this.surface, seed: this.seed, intensity: this.intensity, ...detail }
        : { id: this.artwork, palette: this.palette, surface: this.surface, seed: this.seed, intensity: this.intensity, ...detail },
      {
        durationMs: this.durationMs,
        mode: this.mode,
        motion: 'full',
        autoplay: 'once',
        width: this.canvasSize.width,
        height: this.canvasSize.height,
        dpr: this.dpr,
        easing: this.easing === 'engine' ? undefined : EASING_FNS[this.easing],
        tail: this.tail,
        quality: this.quality,
        simResolution: this.simOverride || undefined,
      },
    );
    this.player = player;
    const sync = () => this.syncFromPlayer();
    player.on('ready', () => {
      sync();
      this.markFirstPaint();
    });
    player.on('statechange', sync);
    player.on('finished', sync);
    player.on('fallback', sync);
    player.on('error', (error) => {
      this.error = error;
      sync();
    });
  }

  play(): void {
    if (this.finished) this.player?.reset();
    this.player?.play();
    this.syncFromPlayer();
  }

  pause(): void {
    this.player?.pause();
    this.syncFromPlayer();
  }

  reset(): void {
    this.player?.reset();
    this.syncFromPlayer();
  }

  finish(): void {
    this.player?.finishImmediately();
    this.syncFromPlayer();
  }

  scrub(value: number): void {
    this.scrubbing = true;
    this.player?.seek(value);
    this.progress = Math.min(1, Math.max(0, value));
    this.playing = false;
    queueMicrotask(() => {
      this.scrubbing = false;
    });
  }

  async exportPng(): Promise<Blob> {
    if (!this.player) throw new Error('no player');
    this.exporting = true;
    try {
      const bytes = await this.player.exportPng(this.canvasSize.width, this.canvasSize.height);
      return new Blob([bytes as BlobPart], { type: 'image/png' });
    } finally {
      this.exporting = false;
      this.syncFromPlayer();
    }
  }

  async exportBaked(): Promise<{ strip: Blob; manifest: Blob; manifestData: BakedManifest }> {
    if (!this.player) throw new Error('no player');
    this.exporting = true;
    try {
      const { strip, manifest } = await this.player.exportStrip(STRIP_FRAMES, STRIP_EDGE);
      return {
        strip: new Blob([strip as BlobPart], { type: 'image/png' }),
        manifest: new Blob([JSON.stringify(manifest)], { type: 'application/json' }),
        manifestData: manifest,
      };
    } finally {
      this.exporting = false;
      this.syncFromPlayer();
    }
  }

  private syncFromPlayer(): void {
    const state = this.player?.state;
    if (!state) return;
    this.playing = state.playing;
    this.finished = state.finished;
    if (!this.scrubbing) this.progress = state.progress;
    this.resolvedMode = state.mode;
    this.fallbackReason = state.fallbackReason;
    this.resolvedDetail = state.detail;
    this.simResolution = state.simResolution;
    this.totalTicks = state.totalTicks;
  }

  private refreshStats(): void {
    this.stats = {
      engine: getEngineHost().stats(),
      frames: getScheduler().stats(),
      firstPaintMs: window.__watercolour?.firstPaintMs,
      gpuInitMs: window.__watercolour?.gpuInitMs,
    };
    this.ensureAdapter();
    if (this.playing) this.syncFromPlayer();
  }

  /** The wasm's `adapterName` is empty on the WebGPU backend, so fall back to `GPUAdapterInfo`. */
  private ensureAdapter(): void {
    const fromEngine = this.stats.engine?.adapterName;
    if (fromEngine && fromEngine.trim()) {
      this.adapter = fromEngine;
      return;
    }
    if (this.adapter) return;
    this.adapterPromise ??= (async (): Promise<string> => {
      try {
        if (typeof navigator === 'undefined' || !navigator.gpu) return 'unknown adapter';
        const adapter = await navigator.gpu.requestAdapter();
        const info = adapter?.info;
        const name =
          info?.description || [info?.vendor, info?.architecture].filter(Boolean).join(' · ') || info?.device;
        return name || 'unknown adapter';
      } catch {
        return 'unknown adapter';
      }
    })();
    this.adapterPromise.then((name) => {
      this.adapter = name;
    });
  }

  private markFirstPaint(): void {
    if (typeof window === 'undefined' || !import.meta.env.DEV) return;
    requestAnimationFrame(() => {
      const w = window;
      w.__watercolour ??= { marks: () => performance.getEntriesByType('mark') };
      if (w.__watercolour.firstPaintMs === undefined && this.resolvedMode === 'live') {
        performance.mark('watercolour:first-paint');
        w.__watercolour.firstPaintMs = performance.now();
        w.__watercolour.gpuInitMs = getEngineHost().stats()?.initMs;
      }
    });
  }
}