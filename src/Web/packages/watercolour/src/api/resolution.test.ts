import { describe, expect, it } from 'vitest';
import { detailForEdge } from '../types';

describe('detailForEdge (backing long edge to detail tier)', () => {
  it('steps through the four tiers at the documented thresholds', () => {
    expect(detailForEdge(0)).toBe('small');
    expect(detailForEdge(63)).toBe('small');
    expect(detailForEdge(64)).toBe('medium');
    expect(detailForEdge(191)).toBe('medium');
    expect(detailForEdge(192)).toBe('large');
    expect(detailForEdge(319)).toBe('large');
    expect(detailForEdge(320)).toBe('extraLarge');
    expect(detailForEdge(900)).toBe('extraLarge');
  });

  it('treats a negative edge as small', () => {
    expect(detailForEdge(-5)).toBe('small');
  });
});
