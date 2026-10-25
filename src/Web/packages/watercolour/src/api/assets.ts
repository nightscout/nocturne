import type { ArtworkId, PaletteId, Surface } from '../types';
import { type BakedManifest, parseBakedManifest } from './baked';
import { WatercolourError } from './errors';
import { iconAssetId, sourcePalette } from './palette-defaults';
import { paletteKey } from './scenes';

export { DEFAULT_PALETTE, defaultPaletteFor } from './palette-defaults';

export type AssetVariant = 'final' | 'final-small' | 'strip' | 'manifest';

/**
 * Every image ships as WebP, and the 512 px final also as AVIF, which the
 * loader asks for where the browser decodes it ({@link imageFormat}). The
 * finals are 2.6 MB as WebP and 1.7 MB as AVIF at an alpha error of at most
 * 7/255; the 128 px finals and the strips are smaller as WebP, so they ship
 * only as that (`scripts/to-avif.mjs` has the numbers).
 */
const IMAGE_NAMES: Record<Exclude<AssetVariant, 'manifest'>, string> = {
  final: 'final-512',
  'final-small': 'final-128',
  strip: 'strip',
};

/** The variants shipped as AVIF too. */
export const AVIF_VARIANTS: ReadonlySet<AssetVariant> = new Set(['final']);

export type ImageFormat = 'avif' | 'webp';

function fileName(variant: AssetVariant, format: ImageFormat): string {
  return variant === 'manifest' ? 'strip.json' : `${IMAGE_NAMES[variant]}.${format}`;
}

/** A 1x1 AVIF with alpha: decoding it is the support test, since a browser can parse AVIF and still refuse its alpha. */
const AVIF_PROBE =
  'AAAAHGZ0eXBhdmlmAAAAAG1pZjFhdmlmbWlhZgAAAXBtZXRhAAAAAAAAACFoZGxyAAAAAAAAAABwaWN0AAAAAAAAAAAAAAAAAAAAAA5waXRtAAAAAAABAAAANGlsb2MAAAAAREAAAgABAAAAAAGUAAEAAAAAAAAAGAACAAAAAAGsAAEAAAAAAAAAEAAAADhpaW5mAAAAAAACAAAAFWluZmUCAAAAAAEAAGF2MDEAAAAAFWluZmUCAAAAAAIAAGF2MDEAAAAAr2lwcnAAAACKaXBjbwAAAAxhdjFDgSACAAAAABRpc3BlAAAAAAAAAAEAAAABAAAAEHBpeGkAAAAAAwgICAAAAAxhdjFDgQAcAAAAAA5waXhpAAAAAAEIAAAAOGF1eEMAAAAAdXJuOm1wZWc6bXBlZ0I6Y2ljcDpzeXN0ZW1zOmF1eGlsaWFyeTphbHBoYQAAAAAdaXBtYQAAAAAAAAACAAEDgQIDAAIEhAIFhgAAABppcmVmAAAAAAAAAA5hdXhsAAIAAQABAAAAMG1kYXQSAAoHOAAOUBDQaTILGAAAAEAADBc40zoSAAoEGAAOVTIGGAAAARAP';

let format: Promise<ImageFormat> | undefined;

/**
 * `avif` where the browser decodes the probe with `createImageBitmap`, the
 * call every still and strip is decoded with; else `webp`. Probed once per
 * page.
 */
export function imageFormat(): Promise<ImageFormat> {
  format ??= (async (): Promise<ImageFormat> => {
    if (typeof createImageBitmap !== 'function' || typeof atob !== 'function') return 'webp';
    try {
      const bytes = Uint8Array.from(atob(AVIF_PROBE), (c) => c.charCodeAt(0));
      const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/avif' }));
      bitmap.close?.();
      return 'avif';
    } catch {
      return 'webp';
    }
  })();
  return format;
}

/** Forgets the probed format, for tests. */
export function resetImageFormat(): void {
  format = undefined;
}

/** The bundled asset key for a baked Lucide icon: `assets/lucide-<name>/...`. */
export function iconAssetKey(name: string, palette?: PaletteId, surface?: Surface): AssetKey {
  return { id: iconAssetId(name), palette, surface };
}

export interface AssetOptions {
  /** Serve assets from `<assetBaseUrl>/<artwork>/<paletteKey>/<file>` instead of the bundle. */
  assetBaseUrl?: string;
  /** Explicit URLs override both the bundle and `assetBaseUrl`. */
  assets?: Partial<Record<AssetVariant, string>>;
}

export interface AssetKey {
  id: ArtworkId | string;
  palette?: PaletteId;
  surface?: Surface;
}

let bundle: Promise<typeof import('./bundled-assets')> | undefined;

/** The bundled-asset URL map; see `bundled-assets.ts` for why it loads on demand. */
function loadBundle(): Promise<typeof import('./bundled-assets')> {
  bundle ??= import('./bundled-assets');
  return bundle;
}

/** Whether the bundle ships this asset; `format` picks the image encoding, WebP by default. */
export async function hasBundledAsset(key: AssetKey, variant: AssetVariant, format: ImageFormat = 'webp'): Promise<boolean> {
  return (await loadBundle()).hasBundled(key, fileName(variant, format));
}

/**
 * Whether a fallback of this kind can be attempted at all; a base URL is
 * trusted without probing. Only a bundled asset loads the URL map to answer.
 */
export async function assetAvailable(key: AssetKey, variant: AssetVariant, options: AssetOptions = {}): Promise<boolean> {
  if (options.assets?.[variant]) return true;
  if (options.assetBaseUrl) return true;
  return hasBundledAsset(key, variant);
}

export async function assetUrl(key: AssetKey, variant: AssetVariant, options: AssetOptions = {}): Promise<string | undefined> {
  const explicit = options.assets?.[variant];
  if (explicit) return explicit;
  const preferred = fileName(variant, AVIF_VARIANTS.has(variant) ? await imageFormat() : 'webp');
  if (options.assetBaseUrl) {
    const base = trimTrailingSlashes(options.assetBaseUrl);
    return `${base}/${key.id}/${paletteKey(sourcePalette(key.id, key.palette), key.surface)}/${preferred}`;
  }
  const bundle = await loadBundle();
  return bundle.bundledUrl(key, preferred) ?? bundle.bundledUrl(key, fileName(variant, 'webp'));
}

/** A scan, not `/\/+$/`: that pattern backtracks quadratically on a long run of interior slashes. */
function trimTrailingSlashes(url: string): string {
  let end = url.length;
  while (end > 0 && url[end - 1] === '/') end--;
  return url.slice(0, end);
}

export async function loadManifest(url: string): Promise<BakedManifest> {
  const response = await fetch(url);
  if (!response.ok) throw new WatercolourError('AssetMissing', `${url}: HTTP ${response.status}`);
  return parseBakedManifest(await response.json());
}

/** Artworks with at least one bundled asset set, for galleries and tests. */
export async function bundledArtworkIds(): Promise<string[]> {
  return (await loadBundle()).bundledIds();
}
