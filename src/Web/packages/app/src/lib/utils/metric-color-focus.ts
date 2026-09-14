import {
  GLUCOSE_HEATMAP_LEGEND_STOPS,
  getGlucoseHeatmapFill,
} from "./chart-colors";

export type ColorFocusRange = readonly [number, number];
export type GlucoseColorThresholds = readonly [number, number, number, number];
export const DEFAULT_GLUCOSE_COLOR_THRESHOLDS: GlucoseColorThresholds = [
  54, 72, 180, 250,
];
export const GLUCOSE_COLOR_MIN = GLUCOSE_HEATMAP_LEGEND_STOPS[0].mgdl;
export const GLUCOSE_COLOR_MAX = GLUCOSE_HEATMAP_LEGEND_STOPS.at(-1)!.mgdl;
export const DEFAULT_GLUCOSE_FOCUS_BAND: ColorFocusRange = [
  GLUCOSE_COLOR_MIN,
  GLUCOSE_COLOR_MAX,
];

export function resolveGlucoseFocusBand(
  candidate: unknown
): ColorFocusRange | null {
  if (!Array.isArray(candidate) || candidate.length !== 2) return null;
  const [min, max] = candidate;
  return typeof min === "number" &&
    typeof max === "number" &&
    Number.isFinite(min) &&
    Number.isFinite(max) &&
    min >= GLUCOSE_COLOR_MIN &&
    max > min &&
    max <= GLUCOSE_COLOR_MAX
    ? [min, max]
    : null;
}

export function resolveGlucoseColorThresholds(
  candidate: unknown
): GlucoseColorThresholds | null {
  if (!Array.isArray(candidate) || candidate.length !== 4) return null;
  if (
    !candidate.every(
      (value) => typeof value === "number" && Number.isFinite(value)
    )
  )
    return null;
  const [a, b, c, d] = candidate;
  return a > GLUCOSE_COLOR_MIN &&
    a < b &&
    b < c &&
    c < d &&
    d < GLUCOSE_COLOR_MAX
    ? [a, b, c, d]
    : null;
}

// Keep the theme's continuous heatmap palette while moving its four color boundaries.
export function glucoseColorFocusStops(candidate: GlucoseColorThresholds) {
  const thresholds =
    resolveGlucoseColorThresholds(candidate) ??
    DEFAULT_GLUCOSE_COLOR_THRESHOLDS;
  if (
    thresholds.every(
      (value, index) => value === DEFAULT_GLUCOSE_COLOR_THRESHOLDS[index]
    )
  )
    return GLUCOSE_HEATMAP_LEGEND_STOPS;
  const source = [
    GLUCOSE_COLOR_MIN,
    ...DEFAULT_GLUCOSE_COLOR_THRESHOLDS,
    GLUCOSE_COLOR_MAX,
  ];
  const target = [GLUCOSE_COLOR_MIN, ...thresholds, GLUCOSE_COLOR_MAX];
  // A boundary inside an existing color segment must split that segment in the legend too.
  const anchors = [
    ...new Set([
      ...GLUCOSE_HEATMAP_LEGEND_STOPS.map((stop) => stop.mgdl),
      ...source,
    ]),
  ].sort((a, b) => a - b);
  return anchors.map((anchor) => {
    const upper = Math.max(
      1,
      source.findIndex((value) => anchor <= value)
    );
    const fraction =
      (anchor - source[upper - 1]) / (source[upper] - source[upper - 1]);
    return {
      mgdl: target[upper - 1] + fraction * (target[upper] - target[upper - 1]),
      color:
        GLUCOSE_HEATMAP_LEGEND_STOPS.find((stop) => stop.mgdl === anchor)
          ?.color ?? getGlucoseHeatmapFill(anchor),
    };
  });
}

export function resolveColorFocusRange(
  candidate: unknown
): ColorFocusRange | null {
  if (!Array.isArray(candidate) || candidate.length !== 2) return null;
  const [min, max] = candidate;
  return typeof min === "number" &&
    typeof max === "number" &&
    Number.isFinite(min) &&
    Number.isFinite(max) &&
    min >= 0 &&
    max > min
    ? [min, max]
    : null;
}

export function getFocusedIntensityFill(
  value: number,
  range: ColorFocusRange,
  cssVar: string,
  lowColor?: string,
  highColor?: string,
  invert = false
): string {
  const [min, max] = resolveColorFocusRange(range) ?? [0, 1];
  let intensity = Number.isFinite(value)
    ? Math.max(0, Math.min((value - min) / (max - min), 1))
    : 0;
  if (invert) intensity = 1 - intensity;
  if (lowColor && highColor) {
    return `color-mix(in srgb, ${highColor} ${Math.round(intensity * 100)}%, ${lowColor})`;
  }
  return `color-mix(in srgb, var(${cssVar}) ${Math.round(15 + intensity * 85)}%, transparent)`;
}

export function colorFocusGradient(
  range: ColorFocusRange,
  domainMax: number,
  cssVar: string,
  lowColor?: string,
  highColor?: string,
  invert = false
): string {
  const validRange = resolveColorFocusRange(range) ?? [0, 1];
  const domain = Math.max(
    Number.isFinite(domainMax) ? domainMax : 1,
    validRange[1]
  );
  const low = getFocusedIntensityFill(validRange[0], validRange, cssVar, lowColor, highColor, invert);
  const high = getFocusedIntensityFill(validRange[1], validRange, cssVar, lowColor, highColor, invert);
  return `linear-gradient(to right, ${low} 0%, ${low} ${(validRange[0] / domain) * 100}%, ${high} ${(validRange[1] / domain) * 100}%, ${high} 100%)`;
}

/** Same anchor positions, colors reversed end-to-end. */
export function reverseStopColors<T extends { mgdl: number; color: string }>(
  stops: ReadonlyArray<T>
): T[] {
  const colors = stops.map((stop) => stop.color);
  return stops.map((stop, index) => ({ ...stop, color: colors[colors.length - 1 - index] }));
}

/** Recolors the glucose ramp between a custom low/high pair, optionally reversed; Theme (no colors) can still be inverted. */
export function applyGlucosePalette(
  stops: ReadonlyArray<{ mgdl: number; color: string }>,
  lowColor?: string,
  highColor?: string,
  invert = false
): ReadonlyArray<{ mgdl: number; color: string }> {
  let result = stops;
  if (lowColor && highColor && stops.length > 0) {
    const min = stops[0].mgdl;
    const max = stops[stops.length - 1].mgdl;
    const domain = Math.max(max - min, 1);
    result = stops.map((stop) => ({
      mgdl: stop.mgdl,
      color: `color-mix(in srgb, ${highColor} ${Math.round(Math.max(0, Math.min(1, (stop.mgdl - min) / domain)) * 100)}%, ${lowColor})`,
    }));
  }
  return invert ? reverseStopColors(result) : result;
}

export function insertSliderSteps(base: readonly number[], extra: readonly number[]): number[] {
  const result = [...base];
  for (const value of extra) {
    let low = 0, high = result.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if (result[middle] < value) low = middle + 1;
      else high = middle;
    }
    if (result[low] !== value) result.splice(low, 0, value);
  }
  return result;
}
