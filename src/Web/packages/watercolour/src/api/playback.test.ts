import { describe, expect, it } from 'vitest';
import type { IconNode } from '../types';
import { autoplayAction, iconStaticBackend } from './playback';

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
