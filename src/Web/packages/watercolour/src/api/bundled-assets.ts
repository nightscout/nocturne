import { defaultPaletteFor, sourcePalette } from './palette-defaults';
import { paletteKey } from './scenes';
import type { AssetKey } from './assets';

/**
 * Bundled assets laid out as `assets/<artwork>/<paletteKey>/<file>`, each
 * rewritten by Vite to its hashed URL. Nothing is fetched until a URL is.
 *
 * The map's keys alone are a third of the library's static weight, so this
 * module is only ever imported dynamically, by `assets.ts`, on the first
 * resolve that may draw a baked or static asset. Within it the URLs are
 * eager: a lazy entry is a JS chunk of its own holding one string, a round
 * trip in front of every still, strip and manifest fetched. `no-inline`
 * keeps the files under Vite's inline limit (most 128 px stills and every
 * manifest) out of the map as data URLs, which would make it 180 KB gzip.
 */
const bundled = import.meta.glob('../../assets/*/*/*.{avif,webp,json}', {
  query: '?url&no-inline',
  import: 'default',
  eager: true,
}) as Record<string, string>;

function bundledPath(key: AssetKey, file: string): string {
  const exact = `../../assets/${key.id}/${paletteKey(sourcePalette(key.id, key.palette), key.surface)}/${file}`;
  if (exact in bundled) return exact;
  const fallback = `../../assets/${key.id}/${paletteKey(defaultPaletteFor(key.id), key.surface)}/${file}`;
  return fallback in bundled ? fallback : exact;
}

export function hasBundled(key: AssetKey, file: string): boolean {
  return bundledPath(key, file) in bundled;
}

export function bundledUrl(key: AssetKey, file: string): string | undefined {
  return bundled[bundledPath(key, file)];
}

export function bundledIds(): string[] {
  const ids = new Set<string>();
  for (const path of Object.keys(bundled)) {
    const match = /^\.\.\/\.\.\/assets\/([^/]+)\//.exec(path);
    if (match) ids.add(match[1]!);
  }
  return Array.from(ids).sort();
}
