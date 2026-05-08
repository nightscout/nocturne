/**
 * Chart color utilities for resolving backend ChartColor enum values to CSS variables
 * ChartColor enum values are kebab-case strings that match CSS custom property names
 */
import { type ChartColor } from '$lib/api';

/**
 * Resolve a ChartColor enum value to a CSS variable reference
 * e.g. "glucose-in-range" → "var(--glucose-in-range)"
 */
export function resolveChartColor(color: ChartColor | string): string {
	return `var(--${color})`;
}

/**
 * Glucose threshold boundaries in mg/dL, defining the four cut-points between
 * the five discrete colour buckets (very-low / low / in-range / high / very-high).
 */
export interface GlucoseThresholds {
	low: number;
	high: number;
	veryLow: number;
	veryHigh: number;
}

/**
 * Get glucose color based on mg/dL value and thresholds
 * This stays on the frontend as it's a display concern (threshold-based coloring per point)
 */
export function getGlucoseColor(
	sgvMgdl: number,
	thresholds: GlucoseThresholds
): string {
	if (sgvMgdl < thresholds.veryLow) return 'var(--glucose-very-low)';
	if (sgvMgdl < thresholds.low) return 'var(--glucose-low)';
	if (sgvMgdl <= thresholds.high) return 'var(--glucose-in-range)';
	if (sgvMgdl <= thresholds.veryHigh) return 'var(--glucose-high)';
	return 'var(--glucose-very-high)';
}

/**
 * Glucose colour mode union. `single` is a caller concern (a fixed colour);
 * `threshold` is the discrete bucket palette; `continuous` is the oklch
 * spectrum interpolation below.
 */
export type GlucoseColorMode = 'single' | 'threshold' | 'continuous';

/**
 * Anchor stops for the continuous glucose spectrum.
 * [mgdl, hue, chroma, lightness] in oklch space.
 *
 * This is the only frontend-computed colour in the codebase. It's a
 * documented exception to the "backend computes colours" rule because the
 * spectrum is presentation, not categorisation. Originally lived in
 * halo-dial/colors.ts; moved here to be the single source of truth for
 * both the halo dial and glucose chart.
 */
const SPECTRUM_STOPS: ReadonlyArray<readonly [number, number, number, number]> = [
	[40, 25, 0.22, 0.58],
	[55, 40, 0.2, 0.62],
	[70, 85, 0.18, 0.78],
	[90, 150, 0.16, 0.74],
	[120, 175, 0.15, 0.72],
	[150, 200, 0.16, 0.72],
	[180, 235, 0.17, 0.7],
	[220, 275, 0.18, 0.65],
	[260, 310, 0.2, 0.62],
	[320, 340, 0.22, 0.58]
];

export const GLUCOSE_SPECTRUM_ANCHORS: ReadonlyArray<number> = SPECTRUM_STOPS.map((s) => s[0]);

/** Continuous oklch interpolation between anchor stops; clamps below/above. */
export function getGlucoseColorContinuous(mgdl: number): string {
	const first = SPECTRUM_STOPS[0];
	const last = SPECTRUM_STOPS[SPECTRUM_STOPS.length - 1];
	let lo = first;
	let hi = last;

	if (mgdl <= first[0]) {
		lo = hi = first;
	} else if (mgdl >= last[0]) {
		lo = hi = last;
	} else {
		for (let i = 0; i < SPECTRUM_STOPS.length - 1; i++) {
			if (mgdl >= SPECTRUM_STOPS[i][0] && mgdl <= SPECTRUM_STOPS[i + 1][0]) {
				lo = SPECTRUM_STOPS[i];
				hi = SPECTRUM_STOPS[i + 1];
				break;
			}
		}
	}

	const t = lo[0] === hi[0] ? 0 : (mgdl - lo[0]) / (hi[0] - lo[0]);
	let h0 = lo[1];
	let h1 = hi[1];
	if (Math.abs(h1 - h0) > 180) {
		if (h1 > h0) h0 += 360;
		else h1 += 360;
	}
	const h = (h0 + (h1 - h0) * t) % 360;
	const c = lo[2] + (hi[2] - lo[2]) * t;
	const l = lo[3] + (hi[3] - lo[3]) * t;
	return `oklch(${l.toFixed(3)} ${c.toFixed(3)} ${h.toFixed(2)})`;
}

/**
 * Resolve a glucose colour by mode. Threshold mode returns a `var(--glucose-*)`
 * CSS variable reference; continuous mode returns an `oklch(...)` string.
 */
export function getGlucoseColorByMode(
	mgdl: number,
	mode: Exclude<GlucoseColorMode, 'single'>,
	thresholds: GlucoseThresholds
): string {
	return mode === 'continuous'
		? getGlucoseColorContinuous(mgdl)
		: getGlucoseColor(mgdl, thresholds);
}
