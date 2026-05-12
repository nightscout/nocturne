import {
	GLUCOSE_SPECTRUM_ANCHORS,
	getGlucoseColorContinuous,
	type GlucoseThresholds
} from '$lib/utils/chart-colors';

export type GradientStop = [offset: number, color: string];

/**
 * Gradient coordinate convention shared by `thresholdLineStops` and
 * `continuousLineStops`: callers MUST apply the resulting stops to a
 * `<LinearGradient vertical units="userSpaceOnUse">` whose y-extent matches
 * `[0, chartHeight]`. Offsets are computed as `axisScale(mgdl) / chartHeight`,
 * so offset 0 is the chart's pixel-y origin (top) and offset 1 is the bottom.
 */

function offsetFor(
	mgdl: number,
	axisScale: (m: number) => number,
	chartHeight: number
): number {
	// axisScale returns chart pixel y; gradient offset is 0 at top, 1 at bottom.
	const px = axisScale(mgdl);
	const o = px / chartHeight;
	return Math.max(0, Math.min(1, o));
}

export function thresholdLineStops(
	t: GlucoseThresholds,
	axisScale: (mgdl: number) => number,
	chartHeight: number
): GradientStop[] {
	const oVH = offsetFor(t.veryHigh, axisScale, chartHeight);
	const oH = offsetFor(t.high, axisScale, chartHeight);
	const oL = offsetFor(t.low, axisScale, chartHeight);
	const oVL = offsetFor(t.veryLow, axisScale, chartHeight);
	return [
		[0, 'var(--glucose-very-high)'],
		[oVH, 'var(--glucose-very-high)'],
		[oVH, 'var(--glucose-high)'],
		[oH, 'var(--glucose-high)'],
		[oH, 'var(--glucose-in-range)'],
		[oL, 'var(--glucose-in-range)'],
		[oL, 'var(--glucose-low)'],
		[oVL, 'var(--glucose-low)'],
		[oVL, 'var(--glucose-very-low)'],
		[1, 'var(--glucose-very-low)']
	];
}

export function continuousLineStops(
	axisScale: (mgdl: number) => number,
	chartHeight: number
): GradientStop[] {
	return GLUCOSE_SPECTRUM_ANCHORS.map(
		(mgdl) =>
			[offsetFor(mgdl, axisScale, chartHeight), getGlucoseColorContinuous(mgdl)] as GradientStop
	);
}

export function fillStopsFromLineStops(
	lineStops: GradientStop[],
	areaOpacity: number
): GradientStop[] {
	return lineStops.map(([offset, color]) => {
		const pct = Math.round((1 - offset) * areaOpacity * 100);
		return [offset, `color-mix(in lch, ${color} ${pct}%, transparent)`] as GradientStop;
	});
}

export function singleColorFillStops(color: string, areaOpacity: number): GradientStop[] {
	const topPct = Math.round(areaOpacity * 100);
	return [
		[0, `color-mix(in lch, ${color} ${topPct}%, transparent)`],
		[1, `color-mix(in lch, ${color} 0%, transparent)`]
	];
}

export function areaY0Accessor(
	areaMode: 'baseline' | 'deviation',
	thresholds: Pick<GlucoseThresholds, 'low' | 'high'>
): ((d: { sgv: number }) => number) | undefined {
	if (areaMode === 'baseline') return undefined;
	return (d) => Math.max(thresholds.low, Math.min(thresholds.high, d.sgv));
}
