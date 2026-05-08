import { describe, it, expect, vi } from 'vitest';

vi.mock('$app/environment', () => ({ browser: false, dev: false }));
vi.mock('$app/navigation', () => ({}));
vi.mock('$app/state', () => ({}));
vi.mock('$app/server', () => ({
	getRequestEvent: vi.fn(),
	query: (fn: unknown) => fn,
	command: (fn: unknown) => fn,
	form: (fn: unknown) => fn
}));
vi.mock('@sveltejs/kit', () => ({
	error: vi.fn(),
	redirect: vi.fn()
}));

const {
	thresholdLineStops,
	continuousLineStops,
	fillStopsFromLineStops,
	singleColorFillStops,
	areaY0Accessor
} = await import('./glucose-gradient-stops');

const thresholds = { veryLow: 55, low: 70, high: 180, veryHigh: 250 };
// axisScale: glucose value → chart pixel y. Chart top = small y, bottom = large y.
const axisScale = (mgdl: number) => 400 - (mgdl / 400) * 400;
const chartHeight = 400;

describe('thresholdLineStops', () => {
	const stops = thresholdLineStops(thresholds, axisScale, chartHeight);

	it('emits 10 stops (2 anchor + 4 transitions × 2)', () => {
		expect(stops).toHaveLength(10);
	});

	it('first stop is at offset 0 with very-high colour', () => {
		expect(stops[0][0]).toBe(0);
		expect(stops[0][1]).toContain('--glucose-very-high');
	});

	it('last stop is at offset 1 with very-low colour', () => {
		expect(stops[9][0]).toBe(1);
		expect(stops[9][1]).toContain('--glucose-very-low');
	});

	it('hard transitions: pairs share an offset', () => {
		expect(stops[1][0]).toBe(stops[2][0]); // veryHigh transition
		expect(stops[3][0]).toBe(stops[4][0]); // high transition
	});

	it('offsets increase monotonically', () => {
		for (let i = 1; i < stops.length; i++) {
			expect(stops[i][0]).toBeGreaterThanOrEqual(stops[i - 1][0]);
		}
	});

	it('computes offsets as axisScale(threshold) / chartHeight', () => {
		// axisScale(180) = 220, chartHeight = 400 → offset 0.55
		expect(stops[3][0]).toBeCloseTo(0.55);
		// axisScale(70) = 330 → offset 0.825
		expect(stops[5][0]).toBeCloseTo(0.825);
	});

	it('clamps thresholds outside the chart domain', () => {
		// axisScale(500) = -100 → clamps to offset 0
		const tightThresholds = { veryLow: 55, low: 70, high: 180, veryHigh: 500 };
		const clampedStops = thresholdLineStops(tightThresholds, axisScale, chartHeight);
		expect(clampedStops).toHaveLength(10);
		for (let i = 1; i < clampedStops.length; i++) {
			expect(clampedStops[i][0]).toBeGreaterThanOrEqual(clampedStops[i - 1][0]);
		}
	});
});

describe('continuousLineStops', () => {
	it('emits one stop per spectrum anchor', () => {
		const stops = continuousLineStops(axisScale, chartHeight);
		expect(stops.length).toBe(10);
		expect(stops[0][1]).toMatch(/^oklch\(/);
	});

	it('offsets descend (low mgdl is at the bottom of the chart)', () => {
		const stops = continuousLineStops(axisScale, chartHeight);
		// GLUCOSE_SPECTRUM_ANCHORS starts at the low end (→ bottom → high offset)
		// and ends at the high end (→ top → low offset)
		expect(stops[0][0]).toBeGreaterThan(stops[stops.length - 1][0]);
	});
});

describe('fillStopsFromLineStops', () => {
	it('wraps every colour in color-mix() with offset-scaled opacity', () => {
		const lineStops: Array<[number, string]> = [
			[0, 'var(--glucose-very-high)'],
			[1, 'var(--glucose-very-low)']
		];
		const fill = fillStopsFromLineStops(lineStops, 0.5);
		expect(fill[0][1]).toBe(
			'color-mix(in lch, var(--glucose-very-high) 50%, transparent)'
		);
		expect(fill[1][1]).toBe(
			'color-mix(in lch, var(--glucose-very-low) 0%, transparent)'
		);
	});

	it('rounds opacity percentage to integer', () => {
		// offset 0.333, areaOpacity 0.6 → (1 - 0.333) * 0.6 * 100 = 40.02 → 40%
		const fill = fillStopsFromLineStops([[0.333, 'var(--c)']], 0.6);
		expect(fill[0][1]).toBe('color-mix(in lch, var(--c) 40%, transparent)');
	});
});

describe('singleColorFillStops', () => {
	it('emits 2-stop fade', () => {
		const stops = singleColorFillStops('var(--glucose-in-range)', 0.5);
		expect(stops).toHaveLength(2);
		expect(stops[0]).toEqual([0, 'color-mix(in lch, var(--glucose-in-range) 50%, transparent)']);
		expect(stops[1]).toEqual([1, 'color-mix(in lch, var(--glucose-in-range) 0%, transparent)']);
	});
});

describe('areaY0Accessor', () => {
	it('returns undefined for baseline (let layerchart default to 0)', () => {
		expect(areaY0Accessor('baseline', thresholds)).toBeUndefined();
	});

	it('clamps to [low, high] for deviation', () => {
		const fn = areaY0Accessor('deviation', thresholds)!;
		expect(fn({ sgv: 50 })).toBe(thresholds.low);
		expect(fn({ sgv: 120 })).toBe(120);
		expect(fn({ sgv: 300 })).toBe(thresholds.high);
	});
});
