import {
	GLUCOSE_SPECTRUM_ANCHORS,
	getGlucoseColorContinuous,
	type GlucoseColorStop,
	type GlucoseThresholds
} from '../colors.js';

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

/** Gradient samples per segment between two neighbouring range anchors, the lower anchor included. */
const BLEND_STEPS_PER_SEGMENT = 4;

export function continuousLineStops(
	axisScale: (mgdl: number) => number,
	chartHeight: number,
	range?: ReadonlyArray<GlucoseColorStop>
): GradientStop[] {
	// SVG clamps each stop's offset up to the largest before it, so stops must
	// ascend: the anchors run low to high glucose, which is bottom to top.
	const samples = range ? rangeSamples(range) : [...GLUCOSE_SPECTRUM_ANCHORS];
	return samples
		.map(
			(mgdl): GradientStop => [
				offsetFor(mgdl, axisScale, chartHeight),
				getGlucoseColorContinuous(mgdl, range)
			]
		)
		.reverse();
}

/** The range anchors and the points that carry the oklch blend between them. */
function rangeSamples(range: ReadonlyArray<GlucoseColorStop>): number[] {
	const anchors = range.map((s) => s.mgdl);
	return anchors.flatMap((from, i) => {
		const to = anchors[i + 1];
		if (to === undefined) return [from];
		return Array.from(
			{ length: BLEND_STEPS_PER_SEGMENT },
			(_, step) => from + ((to - from) * step) / BLEND_STEPS_PER_SEGMENT
		);
	});
}

export function fillStopsFromLineStops(
	lineStops: GradientStop[],
	areaOpacity: number
): GradientStop[] {
	return lineStops.map(([offset, color]): GradientStop => {
		const pct = Math.round((1 - offset) * areaOpacity * 100);
		return [offset, `color-mix(in lch, ${color} ${pct}%, transparent)`];
	});
}

export function solidFillStopsFromLineStops(
	lineStops: GradientStop[],
	areaOpacity: number
): GradientStop[] {
	const pct = Math.round(areaOpacity * 100);
	return lineStops.map(
		([offset, color]): GradientStop => [offset, `color-mix(in lch, ${color} ${pct}%, transparent)`]
	);
}

export function solidSingleColorFillStops(color: string, areaOpacity: number): GradientStop[] {
	const pct = Math.round(areaOpacity * 100);
	const fill = `color-mix(in lch, ${color} ${pct}%, transparent)`;
	return [
		[0, fill],
		[1, fill]
	];
}

export function singleColorFillStops(color: string, areaOpacity: number): GradientStop[] {
	const topPct = Math.round(areaOpacity * 100);
	return [
		[0, `color-mix(in lch, ${color} ${topPct}%, transparent)`],
		[1, `color-mix(in lch, ${color} 0%, transparent)`]
	];
}

export function areaY0Accessor(
	areaMode: 'full' | 'baseline' | 'deviation',
	thresholds: Pick<GlucoseThresholds, 'low' | 'high'>
): ((d: { sgv: number }) => number) | undefined {
	if (areaMode !== 'deviation') return undefined;
	return (d) => Math.max(thresholds.low, Math.min(thresholds.high, d.sgv));
}

export type LineColorMode = 'single' | 'threshold' | 'continuous';
export type AreaMode = 'off' | 'full' | 'baseline' | 'deviation';

/**
 * Line stops for a colour mode; `undefined` in single mode, which strokes flat. `range` is
 * `distinctRangeStops`' answer and applies to continuous mode only.
 */
export function lineStopsFor(
	mode: LineColorMode,
	thresholds: GlucoseThresholds,
	axisScale: (mgdl: number) => number,
	chartHeight: number,
	range?: ReadonlyArray<GlucoseColorStop>
): GradientStop[] | undefined {
	if (mode === 'single') return undefined;
	if (mode === 'threshold') return thresholdLineStops(thresholds, axisScale, chartHeight);
	return continuousLineStops(axisScale, chartHeight, range);
}

/**
 * Fill stops for an area mode. `full` holds the opacity flat to the chart
 * bottom; `baseline` and `deviation` fade it out toward the bottom.
 */
export function fillStopsFor(
	areaMode: AreaMode,
	lineStops: GradientStop[] | undefined,
	lineColor: string,
	areaOpacity: number
): GradientStop[] | undefined {
	if (areaMode === 'off') return undefined;
	if (areaMode === 'full')
		return lineStops
			? solidFillStopsFromLineStops(lineStops, areaOpacity)
			: solidSingleColorFillStops(lineColor, areaOpacity);
	return lineStops
		? fillStopsFromLineStops(lineStops, areaOpacity)
		: singleColorFillStops(lineColor, areaOpacity);
}
