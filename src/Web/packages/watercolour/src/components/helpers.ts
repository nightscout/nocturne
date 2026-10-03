import { subscribePresentation } from '../api/presentation';
import { createArtworkPlayer, type ArtworkPlayer, type PlayerProgressCallback, type PlayerState } from '../api/playback';
import type { WasmModule } from '../api/engine-host';
import type { CropWindow } from '../types';
import { type ArtworkId, type ArtworkOptions, type FitMode, type IconArtworkSource, type Surface, artworkAspect, detailForEdge } from '../types';

export type PlayerReadyCallback = (player: ArtworkPlayer) => void | (() => void);
export type PlayerStateCallback = (state: PlayerState) => void;

/** Where a `contain` canvas sits within a container that does not match its aspect. */
export type FitAnchor = 'center' | 'bottom-left';

export interface MountOptions extends ArtworkOptions {
  /** One of `artwork`, `icon` and `scene` is the source; `scene` wins, then `icon`. */
  artwork?: ArtworkId;
  /** A Lucide icon source. */
  icon?: IconArtworkSource;
  scene?: (module: WasmModule, width: number, height: number, dpr: number) => string;
  crop?: CropWindow;
  surface?: Surface;
  assetBaseUrl?: string;
  blendTicks?: boolean;
  /** Fires once a backend is drawing; the returned cleanup runs with the player's disposal. */
  onReady?: PlayerReadyCallback;
  /** Fires on every player state change, including settling on `none`, where `onReady` never fires. */
  onStateChange?: PlayerStateCallback;
  onProgress?: PlayerProgressCallback;
  /**
   * `contain` (default) or `fill`, or a function deciding per container
   * size, such as {@link bannerFit}.
   */
  fit?: FitMode | ((containerWidth: number, containerHeight: number) => FitMode);
  /** Only for `contain`: where the aspect box sits. */
  fitAnchor?: FitAnchor;
  /** Live renders one frame then releases the engine instance, keeping the pixels. */
  releaseAfterFinish?: boolean;
}

/** The page background the artwork sits on; matches Artwork.svelte's default. */
export function hostSurface(): Surface {
  const doc = typeof document !== 'undefined' ? document : undefined;
  if (doc) {
    if (doc.documentElement.classList.contains('dark')) return 'dark';
    if (doc.documentElement.classList.contains('light')) return 'light';
    if (typeof getComputedStyle === 'function') {
      const scheme = getComputedStyle(doc.documentElement).colorScheme;
      if (scheme === 'dark') return 'dark';
      if (scheme === 'light') return 'light';
    }
  }
  if (typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches) return 'dark';
  return 'light';
}

/**
 * Calls back whenever {@link hostSurface} would answer differently.
 *
 * A component that resolves its assets once in an effect keeps the light-theme
 * still after a theme toggle, because nothing it depends on changed. Anything
 * still on screen across a toggle has to re-resolve, so it watches both the
 * class the host sets and the system preference underneath it.
 */
export function watchSurface(onchange: (surface: Surface) => void): () => void {
  if (typeof document === 'undefined') return () => {};
  let current = hostSurface();
  const check = () => {
    const next = hostSurface();
    if (next === current) return;
    current = next;
    onchange(next);
  };
  const observer = new MutationObserver(check);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'style'] });
  const query = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : undefined;
  query?.addEventListener('change', check);
  return () => {
    observer.disconnect();
    query?.removeEventListener('change', check);
  };
}

export interface FitBox {
  /** CSS pixels. */
  width: number;
  height: number;
  offsetX: number;
  offsetY: number;
}

/** Narrowest host, as width / height, that a 3:1 banner fills rather than contains. */
const BANNER_FILL_MIN_ASPECT = 2.4;

/**
 * Fit for a 3:1 banner painted behind a host. Its washes run horizontally, so a host near 3:1 or
 * wider fills and they reach both ends; a taller host, usually a card, would squash them, so it
 * contains instead.
 */
export function bannerFit(containerWidth: number, containerHeight: number): FitMode {
  return containerWidth / Math.max(1, containerHeight) >= BANNER_FILL_MIN_ASPECT ? 'fill' : 'contain';
}

/**
 * The largest box of `aspect` (width / height) that fits `containerWidth` x
 * `containerHeight`, centred or anchored to the bottom-left.
 */
export function containBox(
  containerWidth: number,
  containerHeight: number,
  aspect: number,
  anchor: FitAnchor = 'center',
): FitBox {
  const width = Math.max(1, Math.min(containerWidth, containerHeight * aspect));
  const height = width / aspect;
  const offsetX = anchor === 'bottom-left' ? 0 : (containerWidth - width) / 2;
  const offsetY = anchor === 'bottom-left' ? containerHeight - height : (containerHeight - height) / 2;
  return { width, height, offsetX, offsetY };
}

function resolveFitMode(options: MountOptions, containerWidth: number, containerHeight: number): FitMode {
  if (typeof options.fit === 'function') return options.fit(containerWidth, containerHeight);
  return options.fit ?? 'contain';
}

function resolveBox(containerWidth: number, containerHeight: number, options: MountOptions): FitBox {
  if (resolveFitMode(options, containerWidth, containerHeight) === 'fill') {
    return { width: containerWidth, height: containerHeight, offsetX: 0, offsetY: 0 };
  }
  const aspect = options.icon || !options.artwork ? 1 : artworkAspect(options.artwork);
  return containBox(containerWidth, containerHeight, aspect, options.fitAnchor ?? 'center');
}

/**
 * Sizes the canvas to `box`. `keepBacking` changes only its CSS box: resizing
 * the backing store clears it, which a released live canvas cannot redraw.
 */
export function applyCanvasFit(canvas: HTMLCanvasElement, box: FitBox, dpr: number, keepBacking = false): void {
  canvas.style.position = 'absolute';
  canvas.style.left = `${box.offsetX}px`;
  canvas.style.top = `${box.offsetY}px`;
  canvas.style.width = `${box.width}px`;
  canvas.style.height = `${box.height}px`;
  if (keepBacking) return;
  canvas.width = Math.max(1, Math.round(box.width * dpr));
  canvas.height = Math.max(1, Math.round(box.height * dpr));
}

/**
 * Maps component props to player options, filling unset props from `defaults`.
 * `fit` and `surface` are mount options, not player options: a component
 * passes them to `mountPlayer` itself, and the input type refuses them here.
 */
export function artworkOptionsFrom(options: ArtworkOptions, defaults: ArtworkOptions = {}): ArtworkOptions {
  return {
    palette: options.palette ?? defaults.palette,
    seed: options.seed ?? defaults.seed,
    intensity: options.intensity ?? defaults.intensity,
    durationMs: options.durationMs ?? defaults.durationMs,
    easing: options.easing ?? defaults.easing,
    tail: options.tail ?? defaults.tail,
    motion: options.motion ?? defaults.motion,
    quality: options.quality ?? defaults.quality,
    mode: options.mode ?? defaults.mode,
    autoplay: options.autoplay ?? defaults.autoplay,
  };
}

export const MAX_COMPONENT_DPR = 2;

export function componentDpr(): number {
  return Math.min(MAX_COMPONENT_DPR, typeof devicePixelRatio === 'number' ? devicePixelRatio : 1);
}

/**
 * How far a released still's box may grow or shrink before it is painted
 * again at its new size. Short of this it is only stretched: a released
 * canvas cannot redraw, and a repaint blanks it until the new frame lands.
 */
export const RELEASED_REPAINT_SCALE = 1.5;

/** A drag resizes every frame; the repaint waits for the size to settle. */
const RELEASED_REPAINT_SETTLE_MS = 200;

/** Whether a released canvas's backing store is far enough off `box` to paint it again. */
export function needsRepaint(canvas: { width: number; height: number }, box: FitBox, dpr: number): boolean {
  const width = Math.max(1, Math.round(box.width * dpr));
  const height = Math.max(1, Math.round(box.height * dpr));
  const ratio = Math.max(width / canvas.width, canvas.width / width, height / canvas.height, canvas.height / height);
  return ratio >= RELEASED_REPAINT_SCALE;
}

/**
 * The canvas actually in the frame. A backend swaps the element in place when
 * the context type changes (a 2D-locked canvas can never give WebGPU and vice
 * versa), so the passed-in `bind:this` reference can go stale; the swap target
 * is always the one inside the frame.
 */
function currentCanvas(frame: HTMLElement, canvas: HTMLCanvasElement): HTMLCanvasElement {
  return frame.querySelector('canvas') ?? canvas;
}

/**
 * Creates a player sized to the artwork's fit box within the frame (in
 * `contain` the box follows the artwork's aspect, centred), re-creates it
 * when a prop changes, and disposes it on unmount.
 *
 * A frame with no area (under `display: none`, say) gets no player until it
 * first has one: a still would otherwise paint and release at 1x1 and only
 * ever be stretched, and a reveal would hold a live slot it can never play.
 */
export function mountPlayer(frame: HTMLElement, canvas: HTMLCanvasElement, options: MountOptions): () => void {
  if (!options.scene && !options.icon && !options.artwork) throw new TypeError('Artwork requires an artwork, icon or scene.');
  const { onReady, onStateChange } = options;
  const dpr = componentDpr();
  let player: ArtworkPlayer | undefined;
  let unready: (() => void) | undefined;
  let repaintTimer: ReturnType<typeof setTimeout> | undefined;

  const stop = () => {
    clearTimeout(repaintTimer);
    unready?.();
    unready = undefined;
    player?.dispose();
    player = undefined;
  };

  const start = (containerWidth: number, containerHeight: number, startFinished = false) => {
    const box = resolveBox(containerWidth, containerHeight, options);
    applyCanvasFit(currentCanvas(frame, canvas), box, dpr);
    const detail = detailForEdge(Math.max(box.width, box.height));
    const surface = options.surface ?? hostSurface();
    const source = options.scene
      ? { scene: (module: WasmModule) => options.scene!(module, box.width, box.height, dpr) }
      : options.icon
      ? {
          icon: options.icon.icon,
          name: options.icon.name,
          hints: options.icon.hints,
          palette: options.palette,
          seed: options.seed,
          intensity: options.intensity,
          surface,
          detail,
        }
      : { id: options.artwork!, palette: options.palette, seed: options.seed, intensity: options.intensity, surface, detail };
    const created = createArtworkPlayer(currentCanvas(frame, canvas), source, {
      durationMs: options.durationMs,
      easing: options.easing,
      tail: options.tail,
      motion: options.motion,
      quality: options.quality,
      mode: options.mode,
      autoplay: options.autoplay,
      releaseAfterFinish: options.releaseAfterFinish,
      startFinished,
      onProgress: options.onProgress,
      blendTicks: options.blendTicks,
      crop: options.crop,
      assetBaseUrl: options.assetBaseUrl,
      width: box.width,
      height: box.height,
      dpr,
    });
    if (onReady) {
      created.on('ready', () => {
        unready = onReady(created) ?? undefined;
      });
    }
    if (onStateChange) created.on('statechange', () => onStateChange(created.state));
    player = created;
  };

  const rect = frame.getBoundingClientRect();
  const area = measuredSize(rect.width, rect.height);
  if (area) start(area.width, area.height);

  const observer = new ResizeObserver((entries) => {
    const content = entries[0]?.contentRect;
    if (!content) return;
    const next = measuredSize(content.width, content.height);
    if (!player) {
      if (next) start(next.width, next.height);
      return;
    }
    const nextBox = resolveBox(next?.width ?? 1, next?.height ?? 1, options);
    const released = player.state.released === true;
    const target = currentCanvas(frame, canvas);
    applyCanvasFit(target, nextBox, dpr, released);
    clearTimeout(repaintTimer);
    if (!released) {
      player.resize(nextBox.width, nextBox.height, dpr);
      return;
    }
    if (!next || !needsRepaint(target, nextBox, dpr)) return;
    // The reveal has been seen; the repaint lands on the finished frame.
    repaintTimer = setTimeout(() => {
      stop();
      start(next.width, next.height, true);
    }, RELEASED_REPAINT_SETTLE_MS);
  });
  observer.observe(frame);
  // The player resolved its mode under the old preference, so it is rebuilt;
  // a reveal already seen is not played again.
  const unsubscribe = subscribePresentation(() => {
    const finished = player?.state.finished === true;
    stop();
    const size = measuredSize(frame.getBoundingClientRect().width, frame.getBoundingClientRect().height);
    if (size) start(size.width, size.height, finished);
  });
  return () => {
    unsubscribe();
    observer.disconnect();
    stop();
  };
}

/** Whole CSS pixels of a measured box, or nothing when it has no area. */
export function measuredSize(width: number, height: number): { width: number; height: number } | undefined {
  const w = Math.round(width);
  const h = Math.round(height);
  return w >= 1 && h >= 1 ? { width: w, height: h } : undefined;
}
