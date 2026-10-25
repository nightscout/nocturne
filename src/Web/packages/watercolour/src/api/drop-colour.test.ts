import { describe, expect, it } from 'vitest';
import { PALETTE_IDS } from '../types';
import { PALETTE_PIGMENTS, SURFACE_TINT_ALPHA, surfaceTint } from './drop-colour';

describe('PALETTE_PIGMENTS (the paint, without reading a pixel)', () => {
  it('covers every palette the engine ships', () => {
    for (const id of PALETTE_IDS) expect(PALETTE_PIGMENTS[id]).toBeDefined();
  });

  it('gives each role a colour a stylesheet can use', () => {
    for (const id of PALETTE_IDS) {
      for (const channel of Object.values(PALETTE_PIGMENTS[id])) {
        expect(channel).toMatch(/^rgb\(\d+, \d+, \d+\)$/);
      }
    }
  });
});

describe('surfaceTint (a surface taking colour from its paint)', () => {
  it('carries the pigment at the alpha asked for', () => {
    expect(surfaceTint('moss', SURFACE_TINT_ALPHA)).toBe(
      PALETTE_PIGMENTS.moss.baseWash.replace('rgb(', 'rgba(').replace(')', `, ${SURFACE_TINT_ALPHA.toFixed(3)})`),
    );
  });

  it('stays a wash rather than a coloured card', () => {
    expect(SURFACE_TINT_ALPHA).toBeLessThan(0.1);
  });

  it('is invisible at zero, so the tint can be transitioned in', () => {
    expect(surfaceTint('water', 0)).toContain('0.000');
  });
});
