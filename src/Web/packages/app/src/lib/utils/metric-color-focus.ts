import {
  GLUCOSE_HEATMAP_LEGEND_STOPS,
  getGlucoseHeatmapFill,
} from "./chart-colors";

export type ColorFocusRange = readonly [number, number];
export type GlucoseColorThresholds = readonly [number, number, number, number];
export const DEFAULT_GLUCOSE_COLOR_THRESHOLDS: GlucoseColorThresholds = [
  54, 70, 180, 250,
];
export const GLUCOSE_COLOR_MIN = GLUCOSE_HEATMAP_LEGEND_STOPS[0].mgdl;
export const GLUCOSE_COLOR_MAX = GLUCOSE_HEATMAP_LEGEND_STOPS.at(-1)!.mgdl;

export const COLOR_FOCUS_METRICS = [
  "tir",
  "bolus",
  "basal",
  "tdd",
  "carbs",
] as const;
export type ColorFocusMetric = (typeof COLOR_FOCUS_METRICS)[number];
export type ColorFocusPreferences = Partial<
  Record<ColorFocusMetric, ColorFocusRange>
> & { avgGlucose?: GlucoseColorThresholds };

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

export function parseColorFocusPreferences(
  raw: string | null
): ColorFocusPreferences {
  try {
    const parsed: unknown = JSON.parse(raw ?? "null");
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed))
      return {};
    const preferences: ColorFocusPreferences = {};
    for (const metric of COLOR_FOCUS_METRICS) {
      const range = resolveColorFocusRange(
        (parsed as Record<string, unknown>)[metric]
      );
      if (range && (metric !== "tir" || range[1] <= 100))
        preferences[metric] = range;
    }
    const glucose = resolveGlucoseColorThresholds(
      (parsed as Record<string, unknown>).avgGlucose
    );
    if (glucose) preferences.avgGlucose = glucose;
    return preferences;
  } catch {
    return {};
  }
}

export function getFocusedIntensityFill(
  value: number,
  range: ColorFocusRange,
  cssVar: string
): string {
  const [min, max] = resolveColorFocusRange(range) ?? [0, 1];
  const intensity = Number.isFinite(value)
    ? Math.max(0, Math.min((value - min) / (max - min), 1))
    : 0;
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
  const low = getFocusedIntensityFill(validRange[0], validRange, cssVar);
  const high = getFocusedIntensityFill(validRange[1], validRange, cssVar);
  return `linear-gradient(to right, ${low} 0%, ${low} ${(validRange[0] / domain) * 100}%, ${high} ${(validRange[1] / domain) * 100}%, ${high} 100%)`;
}
