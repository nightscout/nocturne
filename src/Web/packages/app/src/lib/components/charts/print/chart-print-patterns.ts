/**
 * Monochrome chart patterns. These helpers return a class name that, when
 * `.chart-patterns-on` is active (print, or the always-show-patterns setting),
 * swaps an SVG mark's fill for a `<pattern>` or overlays a texture on an HTML
 * element. Defs live in `ChartPrintPatterns.svelte`; rules live in `app.css`.
 * Glucose ranges bake in the range colour; categorical slots are texture-only.
 */

/** The six glucose buckets, matching the `--glucose-*` CSS variables. */
export type GlucoseRange =
	| 'very-low'
	| 'low'
	| 'in-range'
	| 'tight-range'
	| 'high'
	| 'very-high';

export const GLUCOSE_RANGES: readonly GlucoseRange[] = [
	'very-low',
	'low',
	'in-range',
	'tight-range',
	'high',
	'very-high',
];

/** Number of distinct categorical (non-glucose) print textures available. */
export const CATEGORY_PATTERN_COUNT = 6;

/** Pattern class for an SVG mark filled with a glucose-range colour. */
export function glucosePatternClass(range: GlucoseRange): string {
	return `chart-pat-${range}`;
}

/** Texture-overlay class for an HTML element with a glucose-range background. */
export function glucoseBgPatternClass(range: GlucoseRange): string {
	return `chart-bgpat-${range}`;
}

/**
 * Pattern class for an SVG mark distinguished only by an arbitrary categorical
 * colour. `slot` is a 1-based index into {@link CATEGORY_PATTERN_COUNT} and wraps.
 */
export function categoryPatternClass(slot: number): string {
	return `chart-pat-cat-${categorySlot(slot)}`;
}

/** Map any (0- or 1-based, possibly out-of-range) index onto 1..COUNT. */
function categorySlot(slot: number): number {
	const n = CATEGORY_PATTERN_COUNT;
	return ((Math.trunc(slot) % n) + n) % n || n;
}
