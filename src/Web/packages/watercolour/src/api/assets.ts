import type { ArtworkId, PaletteId, Surface } from '../types';
import { type BakedManifest, parseBakedManifest } from './baked';
import { WatercolourError } from './errors';
import { paletteKey } from './scenes';

export type AssetVariant = 'final' | 'final-small' | 'strip' | 'manifest';

/**
 * WebP, not PNG. The artwork is soft alpha washes, which PNG stores badly: the
 * whole catalogue is 4.4 MB rather than 20 MB, and the eight files one
 * paint-drop surface can draw are 76 KB rather than 260 KB.
 *
 * `scripts/to-webp.mjs` encodes them after the bake, keeping the alpha plane
 * lossless because alpha is what carries the shape.
 */
const FILE_NAMES: Record<AssetVariant, string> = {
  final: 'final-512.webp',
  'final-small': 'final-128.webp',
  strip: 'strip.webp',
  manifest: 'strip.json',
};

/**
 * The palette each artwork ships baked with (`scripts/bake-manifest.json`).
 * The bundle carries one set per artwork, so any other palette falls back to
 * this one's assets rather than missing entirely.
 */
export const DEFAULT_PALETTE: Record<string, PaletteId> = {
  'crescent-moon': 'moonlight',
  'alarm-bell': 'moonlight',
  'linked-rings': 'dusk',
  'report-pages': 'slate',
  'magnifying-glass': 'water',
  'confirmation-mark': 'moss',
  'avatar-wash': 'water',
  'tab-underline': 'ember',
  'selection-edge': 'water',
  'confirmation-background': 'moss',
  'header-motif': 'moonlight',
  'moonlit-shoreline': 'moonlight',
  'distant-mountains': 'slate',
  'connected-shores': 'water',
  'overlapping-shapes': 'dusk',
  'calendar': 'moonlight',
  'clock': 'slate',
  'stopwatch': 'slate',
  'sunrise': 'ember',
  'footprints': 'moss',
  'apple': 'moss',
  'pizza-slice': 'ember',
  'spanner': 'slate',
  'suitcase': 'dusk',
  'paint-palette': 'dusk',
  'key': 'ember',
  'plug': 'slate',
  'apartment': 'slate',
  'world-globe': 'water',
  'github-mark': 'slate',
  'heart': 'ember',
  'blood-drop': 'ember',
  'heart-rate': 'ember',
  'shield': 'water',
  'people-group': 'dusk',
  'exclamation-mark': 'ember',
  'chat-bubble': 'water',
  'phone': 'slate',
  'cgm-sensor': 'water',
  'insulin-pump': 'slate',
  'glucose-gauge': 'ember',
  'ringing-bell': 'ember',
  'lucide-database': 'slate',
  'lucide-server': 'slate',
  'lucide-cpu': 'slate',
  'lucide-fingerprint': 'water',
  'lucide-battery': 'water',
  'lucide-sprout': 'moss',
  'lucide-scale': 'moss',
  'lucide-syringe': 'ember',
  'lucide-flag': 'ember',
  'lucide-megaphone': 'ember',
  'lucide-rocket': 'ember',
  'lucide-book-open': 'moonlight',
};

export function defaultPaletteFor(id: ArtworkId | string): PaletteId {
  return DEFAULT_PALETTE[id] ?? 'moonlight';
}

/** The bundled asset key for a baked Lucide icon: `assets/lucide-<name>/...`. */
export function iconAssetKey(name: string, palette?: PaletteId, surface?: Surface): AssetKey {
  return { id: `lucide-${name}`, palette, surface };
}

/**
 * Bundled assets laid out as `assets/<artwork>/<paletteKey>/<file>`. Vite
 * rewrites each entry to a hashed URL at build time; nothing is fetched until
 * a loader is called.
 */
const bundled = import.meta.glob('../../assets/*/*/*.{webp,json}', {
  query: '?url',
  import: 'default',
}) as Record<string, () => Promise<string>>;

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

function bundledPath(key: AssetKey, variant: AssetVariant): string {
  const exact = `../../assets/${key.id}/${paletteKey(key.palette, key.surface)}/${FILE_NAMES[variant]}`;
  if (exact in bundled) return exact;
  const fallback = `../../assets/${key.id}/${paletteKey(defaultPaletteFor(key.id), key.surface)}/${FILE_NAMES[variant]}`;
  return fallback in bundled ? fallback : exact;
}

export function hasBundledAsset(key: AssetKey, variant: AssetVariant): boolean {
  return bundledPath(key, variant) in bundled;
}

/** Whether a fallback of this kind can be attempted at all; a base URL is trusted without probing. */
export function assetAvailable(key: AssetKey, variant: AssetVariant, options: AssetOptions = {}): boolean {
  if (options.assets?.[variant]) return true;
  if (options.assetBaseUrl) return true;
  return hasBundledAsset(key, variant);
}

export async function assetUrl(key: AssetKey, variant: AssetVariant, options: AssetOptions = {}): Promise<string | undefined> {
  const explicit = options.assets?.[variant];
  if (explicit) return explicit;
  if (options.assetBaseUrl) {
    const base = options.assetBaseUrl.replace(/\/+$/, '');
    return `${base}/${key.id}/${paletteKey(key.palette, key.surface)}/${FILE_NAMES[variant]}`;
  }
  const loader = bundled[bundledPath(key, variant)];
  return loader ? loader() : undefined;
}

export async function loadManifest(url: string): Promise<BakedManifest> {
  const response = await fetch(url);
  if (!response.ok) throw new WatercolourError('AssetMissing', `${url}: HTTP ${response.status}`);
  return parseBakedManifest(await response.json());
}

/** Artworks with at least one bundled asset set, for galleries and tests. */
export function bundledArtworkIds(): string[] {
  const ids = new Set<string>();
  for (const path of Object.keys(bundled)) {
    const match = /^\.\.\/\.\.\/assets\/([^/]+)\//.exec(path);
    if (match) ids.add(match[1]!);
  }
  return Array.from(ids).sort();
}
