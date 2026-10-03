import { afterEach, describe, expect, it, vi } from 'vitest';
import { artworkAspect, detailForEdge, seedFromName } from '../types';
import { artworkOptionsFrom, bannerFit, containBox, hostSurface } from './helpers';
import heroSource from './ArtworkHero.svelte?raw';
import confirmationSource from './ConfirmationBackground.svelte?raw';

describe('detailForEdge (size to detail)', () => {
  it('maps the backing long edge to the catalogue detail level', () => {
    expect(detailForEdge(0)).toBe('small');
    expect(detailForEdge(32)).toBe('small');
    expect(detailForEdge(63)).toBe('small');
    expect(detailForEdge(64)).toBe('medium');
    expect(detailForEdge(96)).toBe('medium');
    expect(detailForEdge(191)).toBe('medium');
    expect(detailForEdge(192)).toBe('large');
    expect(detailForEdge(319)).toBe('large');
    expect(detailForEdge(320)).toBe('extraLarge');
    expect(detailForEdge(512)).toBe('extraLarge');
  });
});

describe('bannerFit', () => {
  it('fills a host near or wider than the banner, so the washes reach both ends', () => {
    expect(bannerFit(300, 100)).toBe('fill');
    expect(bannerFit(240, 100)).toBe('fill');
    expect(bannerFit(390, 70)).toBe('fill');
  });

  it('contains a host taller than the banner, so the washes are not squashed', () => {
    expect(bannerFit(230, 100)).toBe('contain');
    expect(bannerFit(320, 240)).toBe('contain');
  });

  it('is the confirmation background unless the host picks a fit', () => {
    expect(confirmationSource).toContain('fit: fit ?? bannerFit');
  });
});

describe('seedFromName (name to seed)', () => {
  it('is deterministic per name', () => {
    expect(seedFromName('Sam Okafor')).toBe(seedFromName('Sam Okafor'));
  });

  it('differs between names', () => {
    expect(seedFromName('Sam Okafor')).not.toBe(seedFromName('Priya Natarajan'));
  });

  it('matches FNV-1a for the empty string', () => {
    expect(seedFromName('')).toBe(0x811c9dc5);
  });

  it('stays within uint32 range', () => {
    const seed = seedFromName('a deliberately long name that wraps the hash more than once');
    expect(seed).toBeGreaterThanOrEqual(0);
    expect(seed).toBeLessThanOrEqual(0xffffffff);
  });
});

describe('artworkOptionsFrom (prop to option)', () => {
  it('passes through every provided option', () => {
    const options = artworkOptionsFrom({
      palette: 'ember',
      seed: 7,
      intensity: 0.5,
      durationMs: 900,
      motion: 'full',
      quality: 'high',
      mode: 'baked',
      autoplay: 'never',
    });
    expect(options).toEqual({
      palette: 'ember',
      seed: 7,
      intensity: 0.5,
      durationMs: 900,
      motion: 'full',
      quality: 'high',
      mode: 'baked',
      autoplay: 'never',
    });
  });

  it('applies defaults for unset options', () => {
    const options = artworkOptionsFrom({}, { mode: 'static', autoplay: 'once' });
    expect(options.mode).toBe('static');
    expect(options.autoplay).toBe('once');
  });

  it('lets an explicit option win over its default', () => {
    const options = artworkOptionsFrom({ mode: 'live' }, { mode: 'static' });
    expect(options.mode).toBe('live');
  });

  it('refuses the mount-only options it would drop', () => {
    // A surface or fit passed here never reached the player; `pnpm check` holds these errors.
    // @ts-expect-error surface is a mount option
    const surfaced = artworkOptionsFrom({ surface: 'dark' });
    // @ts-expect-error fit is a mount option
    const fitted = artworkOptionsFrom({ fit: 'fill' });
    expect(surfaced).not.toHaveProperty('surface');
    expect(fitted).not.toHaveProperty('fit');
  });
});

describe('hostSurface', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubHost(classes: string[], scheme: string, darkPrefers: boolean): void {
    vi.stubGlobal('document', {
      documentElement: {
        classList: {
          contains: (name: string) => classes.includes(name),
        },
      },
    });
    vi.stubGlobal('getComputedStyle', () => ({ colorScheme: scheme }));
    vi.stubGlobal('matchMedia', () => ({ matches: darkPrefers }));
  }

  it('defaults to light with no DOM or dark preference', () => {
    expect(hostSurface()).toBe('light');
  });

  it('lets a `.dark` class on <html> win', () => {
    stubHost(['dark'], 'light', false);
    expect(hostSurface()).toBe('dark');
  });

  it('lets a `.light` class beat a dark preference', () => {
    stubHost(['light'], 'light', true);
    expect(hostSurface()).toBe('light');
  });

  it('lets a computed `color-scheme: light` beat a dark preference', () => {
    stubHost([], 'light', true);
    expect(hostSurface()).toBe('light');
  });

  it('lets a `.light` class beat a computed `color-scheme: dark`', () => {
    stubHost(['light'], 'dark', false);
    expect(hostSurface()).toBe('light');
  });

  it('lets a computed `color-scheme: dark` win when no class is set', () => {
    stubHost([], 'dark', false);
    expect(hostSurface()).toBe('dark');
  });

  it('falls back to prefers-color-scheme when no class or color-scheme signal', () => {
    stubHost([], '', true);
    expect(hostSurface()).toBe('dark');
  });

  it('stays light when every signal is light or absent', () => {
    stubHost([], 'light', false);
    expect(hostSurface()).toBe('light');
  });
});

describe('artworkAspect (per-artwork aspect table)', () => {
  it('keeps icons square and scenes/accents at their authored ratio', () => {
    expect(artworkAspect('crescent-moon')).toBe(1);
    expect(artworkAspect('avatar-wash')).toBe(1);
    expect(artworkAspect('tab-underline')).toBe(8);
    expect(artworkAspect('selection-edge')).toBe(1 / 6);
    expect(artworkAspect('confirmation-background')).toBe(3);
    expect(artworkAspect('distant-mountains')).toBe(2);
    expect(artworkAspect('moonlit-shoreline')).toBeCloseTo(16 / 9, 5);
  });
});

describe('containBox (aspect-fit canvas box)', () => {
  it('fits the largest box of the aspect inside the container and centres it', () => {
    expect(containBox(743, 128, 2)).toEqual({ width: 256, height: 128, offsetX: 243.5, offsetY: 0 });
    const box = containBox(235, 172, 3);
    expect(box.width).toBe(235);
    expect(box.height).toBeCloseTo(235 / 3, 5);
    expect(box.offsetX).toBe(0);
    expect(box.offsetY).toBeCloseTo((172 - 235 / 3) / 2, 5);
  });

  it('fills the container when the aspect already matches', () => {
    expect(containBox(160, 32, 5)).toEqual({ width: 160, height: 32, offsetX: 0, offsetY: 0 });
  });

  it('anchors to the bottom-left instead of centring', () => {
    const box = containBox(235, 172, 3, 'bottom-left');
    expect(box.width).toBe(235);
    expect(box.height).toBeCloseTo(235 / 3, 5);
    expect(box.offsetX).toBe(0);
    expect(box.offsetY).toBeCloseTo(172 - 235 / 3, 5);
  });
});
describe('ArtworkHero stacked layout', () => {
  it('feeds aspect-ratio only properties set without units', () => {
    const source = heroSource;
    const set = new Map([...source.matchAll(/style:(--[\w-]+)="([^"]*)"/g)].map((m) => [m[1]!, m[2]!]));
    const ratios = [...source.matchAll(/aspect-ratio:\s*([^;]+);/g)].map((m) => m[1]!.trim()).filter((v) => v !== 'auto');
    expect(ratios.length).toBeGreaterThan(0);
    for (const ratio of ratios) {
      const vars = [...ratio.matchAll(/var\((--[\w-]+)\)/g)].map((m) => m[1]!);
      expect(vars.length).toBeGreaterThan(0);
      for (const name of vars) expect(set.get(name), name).toMatch(/^\{\w+\} \/ \{\w+\}$|^[^a-z%]*$/);
    }
  });
});
