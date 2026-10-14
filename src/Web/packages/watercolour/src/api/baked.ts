import { WatercolourError } from './errors';
import type { CropWindow } from '../types';

const fullPainting: CropWindow = { x: 0, y: 0, width: 1, height: 1 };

/**
 * Baked format: one PNG of `frames` equal frames stacked vertically, evenly
 * spaced in artistic progress. The last frame is finished and dry. The caps
 * bound the decoded bitmap: 16 frames of 256 px is 16 x 256 x 256 x 4 = 4 MB.
 */
export interface BakedManifest {
  version: 1;
  frames: number;
  width: number;
  height: number;
  durationMs: number;
  layout: 'vertical';
}

export const BAKED_MANIFEST_VERSION = 1;
export const MAX_BAKED_FRAMES = 16;
export const MAX_BAKED_FRAME_EDGE = 256;

/**
 * How far a strip frame may be enlarged before the static final is the
 * better source. The final is baked at twice the frame cap, so it always
 * wins beyond this.
 */
export const MAX_BAKED_UPSCALE = 1.5;

/**
 * Whether the baked reveal is a fair source for a canvas this many device
 * pixels on its long edge.
 *
 * Strip frames are capped at `MAX_BAKED_FRAME_EDGE`, and several pieces bake
 * smaller still, so a hero-sized canvas would be enlarging a thumbnail.
 */
export function bakedServesEdge(longEdgeDevicePx: number): boolean {
  return longEdgeDevicePx <= MAX_BAKED_FRAME_EDGE * MAX_BAKED_UPSCALE;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function positiveInteger(value: unknown, max: number): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= max;
}

export function parseBakedManifest(input: unknown): BakedManifest {
  const value = typeof input === 'string' ? safeParse(input) : input;
  if (!isRecord(value)) throw new WatercolourError('InvalidManifest', 'manifest is not an object');
  if (value.version !== BAKED_MANIFEST_VERSION) {
    throw new WatercolourError(
      'UnsupportedVersion',
      `manifest version ${String(value.version)} not supported (this build reads ${BAKED_MANIFEST_VERSION})`,
    );
  }
  if (value.layout !== 'vertical') throw new WatercolourError('InvalidManifest', `layout ${String(value.layout)} not supported`);
  if (!positiveInteger(value.frames, MAX_BAKED_FRAMES)) {
    throw new WatercolourError('InvalidManifest', `frames must be an integer in 1..${MAX_BAKED_FRAMES}`);
  }
  if (!positiveInteger(value.width, MAX_BAKED_FRAME_EDGE) || !positiveInteger(value.height, MAX_BAKED_FRAME_EDGE)) {
    throw new WatercolourError('InvalidManifest', `frame edges must be integers in 1..${MAX_BAKED_FRAME_EDGE}`);
  }
  if (typeof value.durationMs !== 'number' || !(value.durationMs > 0) || !Number.isFinite(value.durationMs)) {
    throw new WatercolourError('InvalidManifest', 'durationMs must be a positive number');
  }
  return {
    version: 1,
    frames: value.frames,
    width: value.width,
    height: value.height,
    durationMs: value.durationMs,
    layout: 'vertical',
  };
}

function safeParse(json: string): unknown {
  try {
    return JSON.parse(json);
  } catch (error) {
    throw new WatercolourError('InvalidManifest', `manifest is not JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
}

export interface StripPosition {
  /** Index of the frame drawn at full opacity. */
  from: number;
  /** Index of the frame cross-faded on top; equals `from` at exact frames. */
  to: number;
  /** Opacity of `to`, 0..1. */
  blend: number;
}

/** Maps artistic progress 0..1 onto the two adjacent strip frames it lies between. */
export function stripFramePosition(progress: number, frames: number): StripPosition {
  const count = Math.max(1, Math.floor(frames));
  const p = Number.isFinite(progress) ? Math.min(1, Math.max(0, progress)) : 1;
  const scaled = p * (count - 1);
  const from = Math.min(count - 1, Math.floor(scaled));
  const to = Math.min(count - 1, from + 1);
  const blend = from === to ? 0 : scaled - from;
  return { from, to, blend };
}

export interface StripBitmap {
  manifest: BakedManifest;
  bitmap: ImageBitmap;
}

/**
 * Decodes the whole strip once. A single `createImageBitmap` costs one decode
 * and one GPU upload; drawing frames is then two `drawImage` calls.
 */
export async function loadStrip(stripUrl: string, manifest: BakedManifest): Promise<StripBitmap> {
  const response = await fetch(stripUrl);
  if (!response.ok) throw new WatercolourError('AssetMissing', `${stripUrl}: HTTP ${response.status}`);
  const blob = await response.blob();
  const bitmap = await createImageBitmap(blob, { premultiplyAlpha: 'premultiply', colorSpaceConversion: 'default' });
  if (bitmap.width !== manifest.width || bitmap.height !== manifest.height * manifest.frames) {
    bitmap.close();
    throw new WatercolourError(
      'InvalidStrip',
      `strip is ${bitmap.width}x${bitmap.height}, manifest describes ${manifest.width}x${manifest.height * manifest.frames}`,
    );
  }
  return { manifest, bitmap };
}

/**
 * Cross-fades the two frames around `progress` into a cleared canvas.
 * `globalAlpha` over transparent pixels is not a true linear blend; at 12
 * frames the difference is below what the eye picks up.
 */
export function drawStripFrame(
  ctx: CanvasRenderingContext2D,
  strip: StripBitmap,
  progress: number,
  width: number,
  height: number,
  crop: CropWindow = fullPainting,
): void {
  const { manifest, bitmap } = strip;
  const { from, to, blend } = stripFramePosition(progress, manifest.frames);
  ctx.clearRect(0, 0, width, height);
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.globalAlpha = 1;
  const sourceX = crop.x * manifest.width;
  const sourceY = crop.y * manifest.height;
  const sourceWidth = crop.width * manifest.width;
  const sourceHeight = crop.height * manifest.height;
  ctx.drawImage(bitmap, sourceX, from * manifest.height + sourceY, sourceWidth, sourceHeight, 0, 0, width, height);
  if (blend > 0) {
    ctx.globalAlpha = blend;
    ctx.drawImage(bitmap, sourceX, to * manifest.height + sourceY, sourceWidth, sourceHeight, 0, 0, width, height);
    ctx.globalAlpha = 1;
  }
}

/** Decodes the final PNG; works with no GPU at all. */
export async function loadStill(url: string): Promise<ImageBitmap> {
  const response = await fetch(url);
  if (!response.ok) throw new WatercolourError('AssetMissing', `${url}: HTTP ${response.status}`);
  return createImageBitmap(await response.blob(), { premultiplyAlpha: 'premultiply' });
}

/**
 * How many decoded stills are kept. Each is up to a megabyte of pixels, and
 * the ones worth keeping are the ones currently on a page.
 */
export const MAX_SHARED_STILLS = 8;

const shared = new Map<string, Promise<ImageBitmap>>();
const sharedStrips = new Map<string, Promise<StripBitmap>>();

/** The least recently used entry goes first once `cache` is past the cap. */
function lend<T>(cache: Map<string, Promise<T>>, key: string, load: () => Promise<T>): Promise<T> {
  const hit = cache.get(key);
  if (hit) {
    // Re-inserted, so the cap evicts whatever has gone longest unused.
    cache.delete(key);
    cache.set(key, hit);
    return hit;
  }
  const pending = load().catch((error: unknown) => {
    // Not cached: the next attempt can succeed where this one did not.
    cache.delete(key);
    throw error;
  });
  cache.set(key, pending);
  if (cache.size > MAX_SHARED_STILLS) cache.delete(cache.keys().next().value!);
  return pending;
}

/**
 * A still decoded once and drawn by everything that needs it.
 *
 * One page can put the same four marks on forty surfaces, and a surface that
 * scrolls out of view and back builds its backend again. Decoding per backend
 * meant two images decoded sixty-nine times over a couple of scrolls: nothing
 * off the network, because the HTTP cache serves them, but 5.6 MB of decode
 * for 160 KB of data.
 *
 * The cache owns what it lends. A borrower draws from the bitmap and never
 * closes it, and eviction drops the reference rather than closing, because a
 * backend may still hold one to redraw on its next resize. {@link loadStill}
 * is untouched for callers that want a bitmap of their own.
 */
export function sharedStill(url: string): Promise<ImageBitmap> {
  return lend(shared, url, () => loadStill(url));
}

/**
 * A strip decoded once and drawn by every baked reveal of the same artwork
 * and palette, on the terms of {@link sharedStill}: a borrower never closes
 * the bitmap. A row of tabs past the live cap no longer decodes one strip per
 * tab.
 */
export function sharedStrip(stripUrl: string, manifest: BakedManifest): Promise<StripBitmap> {
  const key = `${stripUrl}#${manifest.frames}x${manifest.width}x${manifest.height}`;
  return lend(sharedStrips, key, () => loadStrip(stripUrl, manifest));
}

/** Drops every cached still and strip, for tests and for a host reclaiming memory. */
export function clearSharedStills(): void {
  shared.clear();
  sharedStrips.clear();
}

export function drawStill(ctx: CanvasRenderingContext2D, image: ImageBitmap, width: number, height: number, crop: CropWindow = fullPainting): void {
  ctx.clearRect(0, 0, width, height);
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.globalAlpha = 1;
  ctx.drawImage(image, crop.x * image.width, crop.y * image.height, crop.width * image.width, crop.height * image.height, 0, 0, width, height);
}
