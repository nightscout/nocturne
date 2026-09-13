import {
  GLUCOSE_HEATMAP_LEGEND_STOPS,
  GLUCOSE_HEATMAP_OUTSIDE_COLOR,
  getGlucoseHeatmapFill,
} from "./chart-colors";

export type ColorFocusRange = readonly [number, number];
export type GlucoseColorThresholds = readonly [number, number, number, number];
export const DEFAULT_GLUCOSE_COLOR_THRESHOLDS: GlucoseColorThresholds = [
  54, 72, 180, 250,
];
export const GLUCOSE_COLOR_MIN = GLUCOSE_HEATMAP_LEGEND_STOPS[0].mgdl;
export const GLUCOSE_COLOR_MAX = GLUCOSE_HEATMAP_LEGEND_STOPS.at(-1)!.mgdl;

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

export function glucoseColorFocusBand(
  candidate: GlucoseColorThresholds
): ColorFocusRange {
  const thresholds =
    resolveGlucoseColorThresholds(candidate) ??
    DEFAULT_GLUCOSE_COLOR_THRESHOLDS;
  return [thresholds[0], thresholds[3]];
}

export function getFocusedGlucoseFill(
  mgdl: number,
  thresholds: GlucoseColorThresholds,
  stops: ReadonlyArray<{
    mgdl: number;
    color: string;
  }> = GLUCOSE_HEATMAP_LEGEND_STOPS
): string {
  const [low, high] = glucoseColorFocusBand(thresholds);
  if (!Number.isFinite(mgdl) || mgdl < low || mgdl > high) {
    return GLUCOSE_HEATMAP_OUTSIDE_COLOR;
  }
  return getGlucoseHeatmapFill(mgdl, stops);
}

export function glucoseColorFocusGradient(
  stops: ReadonlyArray<{ mgdl: number; color: string }>,
  thresholds: GlucoseColorThresholds,
  min: number = GLUCOSE_COLOR_MIN,
  max: number = GLUCOSE_COLOR_MAX
): string {
  const [low, high] = glucoseColorFocusBand(thresholds);
  const outside = GLUCOSE_HEATMAP_OUTSIDE_COLOR;
  const at = (mgdl: number) => ((mgdl - min) / (max - min)) * 100;
  const ramp = stops
    .filter((stop) => stop.mgdl > low && stop.mgdl < high)
    .map((stop) => `${stop.color} ${at(stop.mgdl)}%`);
  const positions = [
    `${outside} 0%`,
    `${outside} ${at(low)}%`,
    `${getGlucoseHeatmapFill(low, stops)} ${at(low)}%`,
    ...ramp,
    `${getGlucoseHeatmapFill(high, stops)} ${at(high)}%`,
    `${outside} ${at(high)}%`,
    `${outside} 100%`,
  ];
  return `linear-gradient(to right in srgb, ${positions.join(", ")})`;
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

// Clamping the top to full strength instead would leave the largest values permanently
// at the loudest colour, so a narrowed range could only highlight a band and everything
// above it.
export function getFocusedIntensityFill(
  value: number,
  range: ColorFocusRange,
  cssVar: string
): string {
  const [min, max] = resolveColorFocusRange(range) ?? [0, 1];
  const position = (value - min) / (max - min);
  const intensity =
    Number.isFinite(value) && position >= 0 && position <= 1 ? position : 0;
  return `color-mix(in srgb, var(${cssVar}) ${Math.round(15 + intensity * 85)}%, transparent)`;
}

export function colorFocusGradient(
  range: ColorFocusRange,
  domainMax: number,
  cssVar: string
): string {
  const validRange = resolveColorFocusRange(range) ?? [0, 1];
  const domain = Math.max(
    Number.isFinite(domainMax) ? domainMax : 1,
    validRange[1]
  );
  const outside = getFocusedIntensityFill(validRange[0], validRange, cssVar);
  const peak = getFocusedIntensityFill(validRange[1], validRange, cssVar);
  const start = (validRange[0] / domain) * 100;
  const end = (validRange[1] / domain) * 100;
  return `linear-gradient(to right, ${outside} 0%, ${outside} ${start}%, ${peak} ${end}%, ${outside} ${end}%, ${outside} 100%)`;
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
