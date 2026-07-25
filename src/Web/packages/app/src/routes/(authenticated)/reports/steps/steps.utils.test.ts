import { describe, it, expect } from 'vitest';
import { computeDayTotals } from './steps.utils';

describe('computeDayTotals', () => {
  const apr20 = new Date('2026-04-20T00:00:00');
  const apr21 = new Date('2026-04-21T00:00:00');
  const apr22 = new Date('2026-04-22T00:00:00');
  const days = [apr20, apr21, apr22];

  it('sums steps for the correct day', () => {
    const stepCounts = [
      { mills: new Date('2026-04-21T09:00:00').getTime(), metric: 300 },
      { mills: new Date('2026-04-21T17:00:00').getTime(), metric: 500 },
    ];
    const totals = computeDayTotals(stepCounts, days);
    expect(totals.get(apr21.getTime())).toBe(800);
  });

  it('initialises all days to 0', () => {
    const totals = computeDayTotals([], days);
    expect([...totals.values()].every((v) => v === 0)).toBe(true);
  });

  it('ignores step counts outside the days array', () => {
    const stepCounts = [
      { mills: new Date('2026-05-01T12:00:00').getTime(), metric: 999 },
    ];
    const totals = computeDayTotals(stepCounts, days);
    expect([...totals.values()].every((v) => v === 0)).toBe(true);
  });

  it('buckets a reading at exactly midnight to that day', () => {
    const stepCounts = [
      { mills: new Date('2026-04-21T00:00:00').getTime(), metric: 42 },
    ];
    const totals = computeDayTotals(stepCounts, days);
    expect(totals.get(apr21.getTime())).toBe(42);
  });

  it('splits steps across separate days', () => {
    const stepCounts = [
      { mills: new Date('2026-04-20T08:00:00').getTime(), metric: 100 },
      { mills: new Date('2026-04-21T08:00:00').getTime(), metric: 200 },
    ];
    const totals = computeDayTotals(stepCounts, days);
    expect(totals.get(apr20.getTime())).toBe(100);
    expect(totals.get(apr21.getTime())).toBe(200);
  });
});
