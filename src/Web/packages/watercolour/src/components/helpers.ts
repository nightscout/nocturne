import { createArtworkPlayer, type ArtworkPlayer } from '../api/playback';
import { type ArtworkId, type ArtworkOptions, type FitMode, type IconArtworkSource, type Surface, artworkAspect, detailForEdge } from '../types';

export type PlayerReadyCallback = (player: ArtworkPlayer) => void | (() => void);

/** Where a `contain` canvas sits within a container that does not match its aspect. */
export type FitAnchor = 'center' | 'bottom-left';

export interface MountOptions extends ArtworkOptions {
  surface?: Surface;
  assetBaseUrl?: string;
  /** A Lucide icon source; takes precedence over the artwork id. */
  icon?: IconArtworkSource;
  /**
   * `contain` (default) or `fill`, or a function deciding per container
   * size (e.g. ConfirmationBackground fills only near its 3:1 aspect).
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

function resolveBox(
  containerWidth: number,
  containerHeight: number,
  id: ArtworkId | undefined,
  options: MountOptions,
): FitBox {
  if (resolveFitMode(options, containerWidth, containerHeight) === 'fill') {
    return { width: containerWidth, height: containerHeight, offsetX: 0, offsetY: 0 };
  }
  const aspect = options.icon || !id ? 1 : artworkAspect(id);
  return containBox(containerWidth, containerHeight, aspect, options.fitAnchor ?? 'center');
}

export function applyCanvasFit(canvas: HTMLCanvasElement, box: FitBox, dpr: number): void {
  canvas.style.position = 'absolute';
  canvas.style.left = `${box.offsetX}px`;
  canvas.style.top = `${box.offsetY}px`;
  canvas.style.width = `${box.width}px`;
  canvas.style.height = `${box.height}px`;
  canvas.width = Math.max(1, Math.round(box.width * dpr));
  canvas.height = Math.max(1, Math.round(box.height * dpr));
}

/** Maps component props to player options, filling unset props from `defaults`. */
export function artworkOptionsFrom(options: MountOptions, defaults: ArtworkOptions = {}): ArtworkOptions {
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
 * when a prop changes, and disposes it on unmount. `onready` fires once a
 * backend is drawing and its returned cleanup runs with the player's
 * disposal.
 */
export function mountPlayer(
  frame: HTMLElement,
  canvas: HTMLCanvasElement,
  id: ArtworkId | undefined,
  options: MountOptions,
  onready?: PlayerReadyCallback,
): () => void {
  if (!options.icon && !id) throw new TypeError('Artwork requires either `artwork` or `icon`.');
  const dpr = componentDpr();
  const rect = frame.getBoundingClientRect();
  const containerWidth = Math.max(1, Math.round(rect.width));
  const containerHeight = Math.max(1, Math.round(rect.height));
  const box = resolveBox(containerWidth, containerHeight, id, options);
  applyCanvasFit(currentCanvas(frame, canvas), box, dpr);
  const source = options.icon
    ? {
        icon: options.icon.icon,
        name: options.icon.name,
        hints: options.icon.hints,
        palette: options.palette,
        seed: options.seed,
        intensity: options.intensity,
        surface: options.surface ?? hostSurface(),
        detail: detailForEdge(Math.max(box.width, box.height)),
      }
    : {
        id: id!,
        palette: options.palette,
        seed: options.seed,
        intensity: options.intensity,
        surface: options.surface ?? hostSurface(),
        detail: detailForEdge(Math.max(box.width, box.height)),
      };
  const player = createArtworkPlayer(
    currentCanvas(frame, canvas),
    source,
    {
      durationMs: options.durationMs,
      easing: options.easing,
      tail: options.tail,
      motion: options.motion,
      quality: options.quality,
      mode: options.mode,
      autoplay: options.autoplay,
      releaseAfterFinish: options.releaseAfterFinish,
      assetBaseUrl: options.assetBaseUrl,
      width: box.width,
      height: box.height,
      dpr,
    },
  );
  let unready: (() => void) | undefined;
  if (onready) {
    player.on('ready', () => {
      unready = onready(player) ?? undefined;
    });
  }
  const observer = new ResizeObserver((entries) => {
    const content = entries[0]?.contentRect;
    if (!content) return;
    const nextWidth = Math.max(1, Math.round(content.width));
    const nextHeight = Math.max(1, Math.round(content.height));
    const nextBox = resolveBox(nextWidth, nextHeight, id, options);
    applyCanvasFit(currentCanvas(frame, canvas), nextBox, dpr);
    player.resize(nextBox.width, nextBox.height, dpr);
  });
  observer.observe(frame);
  return () => {
    observer.disconnect();
    if (unready) unready();
    player.dispose();
  };
}
