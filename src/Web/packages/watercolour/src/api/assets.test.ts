import { describe, expect, it } from 'vitest';
import manifest from '../../scripts/bake-manifest.json';
import { ARTWORK_ASPECT, type ArtworkId } from '../types';
import { bundledArtworkIds, defaultPaletteFor, hasBundledAsset, iconAssetKey, DEFAULT_PALETTE } from './assets';

/** Manifest ids are `lucide:<name>`; the bundled dirs and DEFAULT_PALETTE keys are `lucide-<name>`. */
const assetId = (id: string) => id.replace(/^lucide:/, 'lucide-');

describe('curated bundle', () => {
  it('falls back to the default palette when the requested one is not baked', () => {
    expect(hasBundledAsset({ id: 'crescent-moon', palette: 'moonlight', surface: 'light' }, 'final')).toBe(true);
    expect(hasBundledAsset({ id: 'crescent-moon', palette: 'ember', surface: 'light' }, 'final')).toBe(true);
    expect(hasBundledAsset({ id: 'crescent-moon', palette: 'ember', surface: 'dark' }, 'final')).toBe(true);
    expect(hasBundledAsset({ id: 'header-motif', palette: 'dusk', surface: 'dark' }, 'strip')).toBe(true);
  });

  it('still reports missing assets for unknown artworks', () => {
    expect(hasBundledAsset({ id: 'does-not-exist' }, 'final')).toBe(false);
    expect(hasBundledAsset({ id: 'crescent-moon', palette: 'moonlight', surface: 'light' }, 'manifest')).toBe(true);
  });

  it('bakes exactly the artworks the manifest lists', () => {
    const manifestIds = manifest.artworks.map((a) => assetId(a.id)).sort();
    expect(bundledArtworkIds()).toEqual(manifestIds);
    for (const id of manifestIds) {
      expect(DEFAULT_PALETTE[id]).toBeDefined();
      expect(defaultPaletteFor(id)).toBe(DEFAULT_PALETTE[id]);
    }
  });

  it('keeps the manifest palettes in sync with DEFAULT_PALETTE', () => {
    for (const artwork of manifest.artworks) {
      expect(DEFAULT_PALETTE[assetId(artwork.id)]).toBe(artwork.palette);
    }
  });

  it('defaults unknown artworks to moonlight', () => {
    expect(defaultPaletteFor('no-such-artwork')).toBe('moonlight');
  });
});

describe('baked lucide icons', () => {
  it('resolves a baked icon to its default-palette assets, with the same fallback rule', () => {
    expect(hasBundledAsset(iconAssetKey('database', 'slate', 'light'), 'final')).toBe(true);
    expect(hasBundledAsset(iconAssetKey('database', 'slate', 'light'), 'strip')).toBe(true);
    expect(hasBundledAsset(iconAssetKey('database', 'ember', 'light'), 'final')).toBe(true);
    expect(hasBundledAsset(iconAssetKey('database', 'ember', 'dark'), 'final')).toBe(true);
    expect(defaultPaletteFor('lucide-database')).toBe('slate');
  });

  it('reports no bundled assets for an icon that is not baked, so it falls to the SVG backend', () => {
    expect(hasBundledAsset(iconAssetKey('clock'), 'final')).toBe(false);
    expect(hasBundledAsset(iconAssetKey('clock'), 'strip')).toBe(false);
  });
});
describe('bake manifest aspect', () => {
  /**
   * The bake renders each still at the manifest's strip aspect, while the
   * components size the canvas by `ARTWORK_ASPECT`. When the two disagree the
   * baked and static rungs are drawn squashed, and a hero's `shape-outside`
   * lands where the paint is not.
   */
  it('asks for strips at the aspect the components lay the artwork out at', () => {
    const wrong: string[] = [];
    for (const spec of manifest.artworks) {
      const id = assetId(spec.id);
      if (!(id in ARTWORK_ASPECT)) continue;
      const declared = ARTWORK_ASPECT[id as ArtworkId];
      const baked = spec.stripWidth / spec.stripHeight;
      // One pixel of rounding on the short edge is unavoidable at strip sizes.
      const tolerance = declared / Math.min(spec.stripWidth, spec.stripHeight);
      if (Math.abs(declared - baked) > tolerance) {
        wrong.push(`${id}: declared ${declared.toFixed(3)}, strip ${spec.stripWidth}x${spec.stripHeight} (${baked.toFixed(3)})`);
      }
    }
    expect(wrong).toEqual([]);
  });
});
