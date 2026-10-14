import { describe, expect, it } from 'vitest';
import type { ArtworkMode, ArtworkMotion } from '../types';
import { fallbackOrder, resolveMode, resolveMotion } from './mode';

const gpu = { webgpu: true, adapter: true, reducedMotion: false };
const noGpu = { webgpu: false, adapter: false, reducedMotion: false };
const noAdapter = { webgpu: true, adapter: false, reducedMotion: false };
const reduced = { ...gpu, reducedMotion: true };

const resolve = (
  requested: ArtworkMode,
  capabilities: typeof gpu,
  assets: { baked: boolean; static: boolean },
  extra: { motion?: ArtworkMotion; capReached?: boolean; releaseAfterFinish?: boolean; customised?: boolean; still?: boolean; liveStill?: boolean; engineReady?: boolean } = {},
) =>
  resolveMode({
    requested,
    motion: extra.motion ?? 'auto',
    capabilities,
    capReached: extra.capReached ?? false,
    hasBaked: assets.baked,
    hasStatic: assets.static,
    releaseAfterFinish: extra.releaseAfterFinish,
    customised: extra.customised,
    still: extra.still,
    liveStill: extra.liveStill,
    engineReady: extra.engineReady,
  });

const all = { baked: true, static: true };
const none = { baked: false, static: false };

describe('resolveMode', () => {
  it('auto: live with a GPU, otherwise baked, otherwise static, otherwise none', () => {
    expect(resolve('auto', gpu, all)).toBe('live');
    expect(resolve('auto', noGpu, all)).toBe('baked');
    expect(resolve('auto', noAdapter, all)).toBe('baked');
    expect(resolve('auto', noGpu, { baked: false, static: true })).toBe('static');
    expect(resolve('auto', noGpu, none)).toBe('none');
    expect(resolve('auto', gpu, none)).toBe('live');
  });

  it('auto: the instance cap pushes the fifth artwork to baked', () => {
    expect(resolve('auto', gpu, all, { capReached: true })).toBe('baked');
    expect(resolve('auto', gpu, { baked: false, static: true }, { capReached: true })).toBe('static');
    expect(resolve('auto', gpu, none, { capReached: true })).toBe('none');
  });

  it('auto: reduced motion prefers the still, then a finished live frame, then baked', () => {
    expect(resolve('auto', reduced, all)).toBe('static');
    expect(resolve('auto', reduced, { baked: true, static: false })).toBe('live');
    expect(resolve('auto', { ...noGpu, reducedMotion: true }, { baked: true, static: false })).toBe('baked');
    expect(resolve('auto', gpu, all, { motion: 'reduced' })).toBe('static');
    expect(resolve('auto', reduced, all, { motion: 'full' })).toBe('live');
  });

  it('auto + releaseAfterFinish + a custom seed: reduced motion runs live (a released single frame), not the still', () => {
    const custom = { releaseAfterFinish: true, customised: true };
    expect(resolve('auto', reduced, all, custom)).toBe('live');
    expect(resolve('auto', { ...noGpu, reducedMotion: true }, all, custom)).toBe('static');
    expect(resolve('auto', { ...noGpu, reducedMotion: true }, { baked: true, static: false }, custom)).toBe('baked');
    expect(resolve('auto', reduced, all, { ...custom, motion: 'full' })).toBe('live');
    expect(resolve('auto', reduced, none, custom)).toBe('live');
    expect(resolve('auto', reduced, all, { ...custom, capReached: true })).toBe('static');
  });

  it('auto + releaseAfterFinish without a custom seed: reduced motion draws the still, as any unseeded artwork does', () => {
    expect(resolve('auto', reduced, all, { releaseAfterFinish: true })).toBe('static');
    expect(resolve('auto', reduced, { baked: true, static: false }, { releaseAfterFinish: true })).toBe('live');
    expect(resolve('auto', gpu, all, { releaseAfterFinish: true })).toBe('live');
  });

  it('still: the static asset whenever one exists, whatever was requested, customised or not', () => {
    const still = { still: true };
    for (const requested of ['auto', 'live', 'baked', 'static'] as const) {
      expect(resolve(requested, gpu, all, still)).toBe('static');
      expect(resolve(requested, gpu, { baked: false, static: true }, { ...still, releaseAfterFinish: true, customised: true })).toBe('static');
      expect(resolve(requested, gpu, all, { ...still, motion: 'full' })).toBe('static');
    }
  });

  it('still without a static asset: the strip`s last frame, then one live frame, then none', () => {
    const still = { still: true };
    expect(resolve('auto', gpu, { baked: true, static: false }, still)).toBe('baked');
    expect(resolve('live', gpu, { baked: true, static: false }, still)).toBe('baked');
    expect(resolve('auto', gpu, none, still)).toBe('live');
    expect(resolve('live', gpu, none, still)).toBe('live');
    expect(resolve('auto', noGpu, none, still)).toBe('none');
    expect(resolve('auto', gpu, none, { ...still, capReached: true })).toBe('none');
    expect(resolve('auto', gpu, none, { ...still, liveStill: false })).toBe('none');
    expect(resolve('auto', gpu, { baked: false, static: true }, { ...still, liveStill: false })).toBe('static');
  });

  it('auto before the engine is ready: the still when one exists, live otherwise', () => {
    const cold = { engineReady: false };
    expect(resolve('auto', gpu, all, cold)).toBe('static');
    expect(resolve('auto', gpu, { baked: false, static: true }, cold)).toBe('static');
    expect(resolve('auto', reduced, all, { ...cold, releaseAfterFinish: true, customised: true })).toBe('static');
    expect(resolve('auto', gpu, { baked: true, static: false }, cold)).toBe('live');
    expect(resolve('auto', gpu, none, cold)).toBe('live');
    expect(resolve('auto', gpu, all, { engineReady: true })).toBe('live');
    expect(resolve('auto', noGpu, all, cold)).toBe('static');
  });

  it('an explicit mode ignores whether the engine is ready', () => {
    const cold = { engineReady: false };
    expect(resolve('live', gpu, all, cold)).toBe('live');
    expect(resolve('baked', gpu, all, cold)).toBe('baked');
  });

  it('explicit modes fall down the chain when unavailable', () => {
    expect(resolve('live', gpu, none)).toBe('live');
    expect(resolve('live', noGpu, all)).toBe('baked');
    expect(resolve('live', noGpu, { baked: false, static: true })).toBe('static');
    expect(resolve('live', noGpu, none)).toBe('none');
    expect(resolve('baked', gpu, all)).toBe('baked');
    expect(resolve('baked', gpu, { baked: false, static: true })).toBe('static');
    expect(resolve('static', gpu, all)).toBe('static');
    expect(resolve('static', gpu, { baked: true, static: false })).toBe('baked');
    expect(resolve('static', gpu, none)).toBe('none');
  });
});

describe('resolveMotion', () => {
  it('follows the system only on auto', () => {
    expect(resolveMotion('auto', true)).toBe('reduced');
    expect(resolveMotion('auto', false)).toBe('full');
    expect(resolveMotion('reduced', false)).toBe('reduced');
    expect(resolveMotion('full', true)).toBe('full');
  });
});

describe('fallbackOrder', () => {
  it('never returns to a higher mode and always ends in none', () => {
    expect(fallbackOrder('live', { hasBaked: true, hasStatic: true })).toEqual(['baked', 'static', 'none']);
    expect(fallbackOrder('live', { hasBaked: false, hasStatic: true })).toEqual(['static', 'none']);
    expect(fallbackOrder('baked', { hasBaked: true, hasStatic: true })).toEqual(['static', 'none']);
    expect(fallbackOrder('static', { hasBaked: true, hasStatic: true })).toEqual(['none']);
  });
});
