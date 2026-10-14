import { afterEach, describe, expect, it, vi } from 'vitest';
import manifest from '../../scripts/bake-manifest.json';
import { ARTWORK_ASPECT, type ArtworkId, type PaletteId } from '../types';
import { bundledUrl } from './bundled-assets';
import { assetUrl, bundledArtworkIds, defaultPaletteFor, hasBundledAsset, iconAssetKey, imageFormat, resetImageFormat, DEFAULT_PALETTE } from './assets';

/** Manifest ids are `lucide:<name>`; the bundled dirs and DEFAULT_PALETTE keys are `lucide-<name>`. */
const assetId = (id: string) => id.replace(/^lucide:/, 'lucide-');

describe('curated bundle', () => {
  it('falls back to the default palette when the requested one is not baked', async () => {
    expect(await hasBundledAsset({ id: 'crescent-moon', palette: 'moonlight', surface: 'light' }, 'final')).toBe(true);
    expect(await hasBundledAsset({ id: 'crescent-moon', palette: 'ember', surface: 'light' }, 'final')).toBe(true);
    expect(await hasBundledAsset({ id: 'crescent-moon', palette: 'ember', surface: 'dark' }, 'final')).toBe(true);
    expect(await hasBundledAsset({ id: 'confirmation-background', palette: 'dusk', surface: 'dark' }, 'strip')).toBe(true);
  });

  it('still reports missing assets for unknown artworks', async () => {
    expect(await hasBundledAsset({ id: 'does-not-exist' }, 'final')).toBe(false);
    expect(await hasBundledAsset({ id: 'crescent-moon', palette: 'moonlight', surface: 'light' }, 'manifest')).toBe(true);
  });

  it('bakes exactly the artworks the manifest lists', async () => {
    const manifestIds = manifest.artworks.map((a) => assetId(a.id)).sort();
    expect(await bundledArtworkIds()).toEqual(manifestIds);
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

  /**
   * The artworks the app draws through `<Artwork artwork=...>`, with the palette
   * it asks for (undefined: the default). Under the `still` presentation one
   * without a still falls through to a live frame and boots the engine on
   * every page that shows it. `cgm-sensor`, an empty-state artwork on
   * feat/watercolour-presentation, joins this list with its bake.
   */
  const DRAWN_BY_THE_APP: ReadonlyArray<[ArtworkId, PaletteId | undefined]> = [
    ['wash', 'slate'],
    ['avatar-wash', 'water'],
    ['selection-edge', 'water'],
    ['tab-underline', 'ember'],
    ...(
      [
        'report-pages', 'clock', 'plug', 'stopwatch', 'chat-bubble', 'shield', 'phone', 'people-group',
        'magnifying-glass', 'key', 'confirmation-mark', 'apple', 'world-globe', 'sunrise', 'suitcase',
        'linked-rings', 'crescent-moon', 'calendar', 'alarm-bell',
      ] as const
    ).map((id): [ArtworkId, undefined] => [id, undefined]),
  ];

  it('has both stills on both surfaces, the 512 px one in both formats, for every artwork the app draws, so the still presentation never boots the engine', async () => {
    const missing: string[] = [];
    for (const [id, palette] of DRAWN_BY_THE_APP) {
      for (const surface of ['light', 'dark'] as const) {
        for (const variant of ['final', 'final-small'] as const) {
          for (const format of variant === 'final' ? (['avif', 'webp'] as const) : (['webp'] as const)) {
            if (!(await hasBundledAsset({ id, palette, surface }, variant, format))) {
              missing.push(`${id}/${palette ?? 'default'}/${surface}/${variant}.${format}`);
            }
          }
        }
      }
    }
    expect(missing).toEqual([]);
  });

  it('holds each URL itself rather than a loader, so no JS chunk stands in front of an asset fetch', () => {
    expect(bundledUrl({ id: 'crescent-moon' }, 'final-128.webp')).toMatch(/final-128[^/]*\.webp/);
    expect(bundledUrl({ id: 'crescent-moon' }, 'strip.json')).toMatch(/strip[^/]*\.json/);
  });

  it('defaults unknown artworks to moonlight', () => {
    expect(defaultPaletteFor('no-such-artwork')).toBe('moonlight');
  });
});

describe('baked lucide icons', () => {
  it('resolves a baked icon to its default-palette assets, with the same fallback rule', async () => {
    expect(await hasBundledAsset(iconAssetKey('database', 'slate', 'light'), 'final')).toBe(true);
    expect(await hasBundledAsset(iconAssetKey('database', 'slate', 'light'), 'strip')).toBe(true);
    expect(await hasBundledAsset(iconAssetKey('database', 'ember', 'light'), 'final')).toBe(true);
    expect(await hasBundledAsset(iconAssetKey('database', 'ember', 'dark'), 'final')).toBe(true);
    expect(defaultPaletteFor('lucide-database')).toBe('slate');
  });

  it('reports no bundled assets for an icon that is not baked, so it falls to the SVG backend', async () => {
    expect(await hasBundledAsset(iconAssetKey('clock'), 'final')).toBe(false);
    expect(await hasBundledAsset(iconAssetKey('clock'), 'strip')).toBe(false);
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

describe('image format', () => {
  afterEach(() => {
    resetImageFormat();
    vi.unstubAllGlobals();
  });

  it('asks for the AVIF final where the browser decodes the probe, and WebP for what ships only as WebP', async () => {
    vi.stubGlobal('createImageBitmap', async () => ({ width: 1, height: 1, close() {} }));
    expect(await assetUrl({ id: 'crescent-moon' }, 'final')).toMatch(/final-512[^/]*\.avif/);
    expect(await assetUrl({ id: 'crescent-moon' }, 'final', { assetBaseUrl: 'https://cdn.example/' })).toBe('https://cdn.example/crescent-moon/moonlight/final-512.avif');
    expect(await assetUrl({ id: 'crescent-moon' }, 'final-small')).toMatch(/final-128[^/]*\.webp/);
    expect(await assetUrl({ id: 'crescent-moon' }, 'strip', { assetBaseUrl: 'https://cdn.example/' })).toBe('https://cdn.example/crescent-moon/moonlight/strip.webp');
    expect(await assetUrl({ id: 'crescent-moon' }, 'manifest')).toMatch(/strip[^/]*\.json/);
  });

  it('falls back to WebP where the probe does not decode', async () => {
    vi.stubGlobal('createImageBitmap', async () => {
      throw new DOMException('unsupported', 'InvalidStateError');
    });
    expect(await assetUrl({ id: 'crescent-moon' }, 'final')).toMatch(/final-512[^/]*\.webp/);
    expect(await assetUrl({ id: 'crescent-moon' }, 'final', { assetBaseUrl: '/art' })).toBe('/art/crescent-moon/moonlight/final-512.webp');
  });

  it('falls back to WebP with no createImageBitmap at all', async () => {
    expect(await imageFormat()).toBe('webp');
  });

  it('probes once per page', async () => {
    const decode = vi.fn(async () => ({ width: 1, height: 1, close() {} }));
    vi.stubGlobal('createImageBitmap', decode);
    await Promise.all([imageFormat(), assetUrl({ id: 'crescent-moon' }, 'final'), assetUrl({ id: 'wash' }, 'final')]);
    expect(await imageFormat()).toBe('avif');
    expect(decode).toHaveBeenCalledOnce();
  });

  it('leaves explicit URLs alone', async () => {
    vi.stubGlobal('createImageBitmap', async () => ({ width: 1, height: 1, close() {} }));
    expect(await assetUrl({ id: 'crescent-moon' }, 'final', { assets: { final: '/mine.png' } })).toBe('/mine.png');
  });
});
