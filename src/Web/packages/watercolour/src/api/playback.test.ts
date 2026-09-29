import { describe, expect, it } from 'vitest';
import type { IconNode } from '../types';
import { EngineHost } from './engine-host';
import { autoplayAction, createArtworkPlayer, iconStaticBackend } from './playback';

const clock: IconNode[] = [
  ['circle', { cx: '12', cy: '12', r: '10' }],
  ['path', { d: 'M12 6v6l4 2' }],
];

describe('iconStaticBackend', () => {
  it('draws the baked final for an icon that has one', () => {
    expect(iconStaticBackend({ icon: clock, name: 'database' }, true)).toBe('baked');
  });

  it('falls to the plain SVG for an icon that is not baked', () => {
    expect(iconStaticBackend({ icon: clock, name: 'clock' }, false)).toBe('svg');
  });

  it('never routes an artwork source to the SVG backend', () => {
    expect(iconStaticBackend(undefined, false)).toBe('baked');
  });
});
describe('autoplayAction', () => {
  it('reveals a releasing player that autoplays, so the slot frees only when the reveal ends', () => {
    expect(autoplayAction('full', undefined, true)).toBe('play');
    expect(autoplayAction('full', 'once', true)).toBe('play');
  });

  it('finishes at once under reduced motion, releasing or not', () => {
    expect(autoplayAction('reduced', 'once', true)).toBe('finish');
    expect(autoplayAction('reduced', 'once', false)).toBe('finish');
  });

  it('finishes a releasing player that never autoplays, so it cannot hold a slot waiting for play()', () => {
    expect(autoplayAction('full', 'never', true)).toBe('finish');
    expect(autoplayAction('full', 'never', false)).toBe('wait');
  });
});

describe('live stills after a failed engine boot', () => {
  it('ends a failed still turn, so the next still resolves', async () => {
    const host = new EngineHost({ loadModule: () => Promise.reject(new Error('wasm fetch failed')) });
    const capabilities = async () => ({ webgpu: true, adapter: true, reducedMotion: true, offscreenCanvas: false });
    const scheduler = { register: () => ({ setActive() {}, dispose() {}, visible: true }) } as never;
    const canvas = () => ({ clientWidth: 32, clientHeight: 32, width: 32, height: 32 }) as unknown as HTMLCanvasElement;
    const options = { engineHost: host, scheduler, capabilities, releaseAfterFinish: true, motion: 'reduced' as const, width: 32, height: 32, dpr: 1 };

    const first = createArtworkPlayer(canvas(), { sceneJson: '{}' }, options);
    await first.ready;
    expect(first.state.mode).toBe('none');
    expect(host.refCount).toBe(0);

    const second = createArtworkPlayer(canvas(), { sceneJson: '{}' }, options);
    const outcome = await Promise.race([
      second.ready.then(() => 'settled'),
      new Promise((resolve) => setTimeout(() => resolve('hung'), 500)),
    ]);
    expect(outcome).toBe('settled');
    expect(second.state.mode).toBe('none');
  });
});
