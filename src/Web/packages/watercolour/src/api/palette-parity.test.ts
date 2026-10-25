import { describe, expect, it } from 'vitest';
import type { ArtworkId, PaletteId, Surface } from '../types';
import { DEFAULT_PALETTE, assetUrl, iconAssetKey } from './assets';
import { type ArtworkRef, type IconRef, paletteKey, resolveSceneJson } from './scenes';

/** The palette argument the live rung hands the engine for `ref`. */
function livePalette(ref: ArtworkRef | IconRef): string {
  let palette = '';
  const module = {
    catalogueScene: (_id: string, _seed: number, p: string) => {
      palette = p;
      return '{"version":1}';
    },
    iconScene: (_elements: string, _name: string, _seed: number, p: string) => {
      palette = p;
      return '{"version":1}';
    },
  };
  resolveSceneJson(module, ref);
  return palette;
}

/** The `<paletteKey>` directory a baked asset URL points into. */
function paletteDir(url: string | undefined, id: string): string | undefined {
  return url && new RegExp(`/${id}/([^/]+)/`).exec(url)?.[1];
}

const SURFACES: Surface[] = ['light', 'dark'];
const artworkIds = Object.keys(DEFAULT_PALETTE).filter((id) => !id.startsWith('lucide-')) as ArtworkId[];
const iconNames = Object.keys(DEFAULT_PALETTE)
  .filter((id) => id.startsWith('lucide-'))
  .map((id) => id.slice('lucide-'.length));

/**
 * The live rung renders the palette it is given and the baked and static rungs
 * draw the set baked for the artwork, so a source that leaves `palette` unset
 * has to resolve to the same palette on every rung, or a reduced-motion or
 * GPU-less visitor sees different colours from a live one.
 */
describe('live and baked palette parity', () => {
  it('paints an artwork with no palette in the palette its assets are baked in', async () => {
    const mismatches: string[] = [];
    for (const id of artworkIds) {
      for (const surface of SURFACES) {
        const ref: ArtworkRef = { id, surface };
        const baked = paletteKey(DEFAULT_PALETTE[id], surface);
        const live = paletteKey(livePalette(ref) as PaletteId, surface);
        const bundled = paletteDir(await assetUrl(ref, 'final'), id);
        const hosted = paletteDir(await assetUrl(ref, 'final', { assetBaseUrl: 'https://cdn.example' }), id);
        if (live !== baked || bundled !== baked || hosted !== baked) {
          mismatches.push(`${id}/${surface}: live ${live}, bundled ${bundled}, hosted ${hosted}, baked ${baked}`);
        }
      }
    }
    expect(mismatches).toEqual([]);
  });

  it('paints a baked icon with no palette in the palette its assets are baked in', async () => {
    const mismatches: string[] = [];
    for (const name of iconNames) {
      for (const surface of SURFACES) {
        const ref: IconRef = { icon: [], name, surface };
        const key = iconAssetKey(name, undefined, surface);
        const baked = paletteKey(DEFAULT_PALETTE[key.id], surface);
        const live = paletteKey(livePalette(ref) as PaletteId, surface);
        const bundled = paletteDir(await assetUrl(key, 'final'), key.id);
        const hosted = paletteDir(await assetUrl(key, 'final', { assetBaseUrl: 'https://cdn.example' }), key.id);
        if (live !== baked || bundled !== baked || hosted !== baked) {
          mismatches.push(`${name}/${surface}: live ${live}, bundled ${bundled}, hosted ${hosted}, baked ${baked}`);
        }
      }
    }
    expect(mismatches).toEqual([]);
  });

  it('keeps an explicit palette on the live rung', () => {
    expect(livePalette({ id: 'confirmation-background', palette: 'water' })).toBe('water');
    expect(livePalette({ icon: [], name: 'database', palette: 'ember' })).toBe('ember');
  });

  it('falls back to moonlight for an icon nothing is baked for', () => {
    expect(livePalette({ icon: [], name: 'clock' })).toBe('moonlight');
  });
});
