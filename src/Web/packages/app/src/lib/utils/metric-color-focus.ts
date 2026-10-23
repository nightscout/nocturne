import { GLUCOSE_HEATMAP_LEGEND_STOPS } from "./chart-colors";

export type ColorFocusRange = readonly [number, number];

/** Pins the color `position` (0 to 1) of the way along a ramp to `value`. */
export type ColorBoundary = { readonly value: number; readonly position: number };

export type RampStop = { readonly position: number; readonly color: string };

export const MAX_COLOR_BOUNDARIES = 16;
export const GLUCOSE_COLOR_MIN = GLUCOSE_HEATMAP_LEGEND_STOPS[0].mgdl;
export const GLUCOSE_COLOR_MAX = GLUCOSE_HEATMAP_LEGEND_STOPS.at(-1)!.mgdl;

/** The heatmap ramp, positioned by its mg/dL anchors so an unwarped scale draws each anchor at its own value. */
export const GLUCOSE_RAMP: readonly RampStop[] = GLUCOSE_HEATMAP_LEGEND_STOPS.map(
  ({ mgdl, color }) => ({
    position: (mgdl - GLUCOSE_COLOR_MIN) / (GLUCOSE_COLOR_MAX - GLUCOSE_COLOR_MIN),
    color,
  })
);

/** A single-hue intensity ramp, faint at the low end so an empty-looking day still reads as data. */
export function metricRamp(cssVar: string): readonly RampStop[] {
  return [
    { position: 0, color: `color-mix(in srgb, var(${cssVar}) 15%, transparent)` },
    { position: 1, color: `var(${cssVar})` },
  ];
}

export interface MetricScale {
  ramp: readonly RampStop[];
  /** The values the ramp spans; beyond it a value takes the end color. */
  domain: ColorFocusRange;
  /** The extent of the slider and of the drawn scale. */
  axis: ColorFocusRange;
  /** Values outside it are dimmed, except at an end that reaches the axis. */
  window: ColorFocusRange;
  boundaries: readonly ColorBoundary[];
  /** How far a value outside the window is faded, 0 to 100. */
  dim: number;
}

export const GLUCOSE_LIMITS: ColorFocusRange = [GLUCOSE_COLOR_MIN, GLUCOSE_COLOR_MAX];

/** The glucose ramp keeps its fixed mg/dL domain, so a color means the same reading whatever the window. */
export function glucoseScale(savedWindow: unknown, savedBoundaries: unknown, dim: number): MetricScale {
  return {
    ramp: GLUCOSE_RAMP,
    domain: GLUCOSE_LIMITS,
    axis: GLUCOSE_LIMITS,
    window: resolveColorFocusRange(savedWindow, GLUCOSE_LIMITS) ?? GLUCOSE_LIMITS,
    boundaries: resolveColorBoundaries(savedBoundaries, GLUCOSE_LIMITS) ?? [],
    dim,
  };
}

/**
 * A metric with no fixed meaning per value spans its ramp across the window, which defaults to
 * zero through the largest value seen. The axis reaches past a saved window that is smaller.
 */
export function intensityScale(
  cssVar: string,
  limits: ColorFocusRange,
  observedMax: number,
  savedWindow: unknown,
  savedBoundaries: unknown,
  dim: number
): MetricScale {
  const max = Number.isFinite(limits[1])
    ? limits[1]
    : Number.isFinite(observedMax) && observedMax > 0
      ? observedMax
      : 1;
  const automatic: ColorFocusRange = [limits[0], max];
  const window = resolveColorFocusRange(savedWindow, limits) ?? automatic;
  // Checked against the limits, not the window: a boundary the window no longer reaches is
  // kept, and only the ramp ignores it.
  const boundaries = resolveColorBoundaries(savedBoundaries, limits) ?? [];
  return {
    ramp: metricRamp(cssVar),
    domain: window,
    axis: [
      limits[0],
      Number.isFinite(limits[1])
        ? limits[1]
        : Math.max(max, window[1], boundaries.at(-1)?.value ?? 0),
    ],
    window,
    boundaries,
    dim,
  };
}

export function resolveColorFocusRange(
  candidate: unknown,
  limits: ColorFocusRange = [0, Infinity]
): ColorFocusRange | null {
  if (!Array.isArray(candidate) || candidate.length !== 2) return null;
  const [min, max] = candidate;
  return typeof min === "number" &&
    typeof max === "number" &&
    Number.isFinite(min) &&
    Number.isFinite(max) &&
    min >= limits[0] &&
    max > min &&
    max <= limits[1]
    ? [min, max]
    : null;
}

/** Boundaries strictly inside `limits`, strictly increasing in both value and position. */
export function resolveColorBoundaries(
  candidate: unknown,
  limits: ColorFocusRange
): ColorBoundary[] | null {
  if (!Array.isArray(candidate) || candidate.length > MAX_COLOR_BOUNDARIES) return null;
  const boundaries: ColorBoundary[] = [];
  for (const item of candidate) {
    if (typeof item !== "object" || item === null || !("value" in item) || !("position" in item))
      return null;
    const { value, position } = item;
    if (
      typeof value !== "number" ||
      typeof position !== "number" ||
      !Number.isFinite(value) ||
      !Number.isFinite(position) ||
      value <= limits[0] ||
      value >= limits[1] ||
      position <= 0 ||
      position >= 1
    )
      return null;
    const previous = boundaries.at(-1);
    if (previous && (value <= previous.value || position <= previous.position)) return null;
    boundaries.push({ value, position });
  }
  return boundaries;
}

function anchors(domain: ColorFocusRange, boundaries: readonly ColorBoundary[]) {
  return [
    { value: domain[0], position: 0 },
    ...boundaries.filter((b) => b.value > domain[0] && b.value < domain[1]),
    { value: domain[1], position: 1 },
  ];
}

/** Where `value` falls along the ramp, piecewise linear between the boundaries. */
export function rampPosition(
  value: number,
  domain: ColorFocusRange,
  boundaries: readonly ColorBoundary[] = []
): number {
  if (!(value > domain[0])) return 0;
  if (value >= domain[1]) return 1;
  const points = anchors(domain, boundaries);
  const upper = points.findIndex((point) => value <= point.value);
  const lo = points[upper - 1];
  const hi = points[upper];
  return lo.position + ((value - lo.value) / (hi.value - lo.value)) * (hi.position - lo.position);
}

/** The value drawn at `position` along the ramp; the inverse of {@link rampPosition}. */
export function rampValue(
  position: number,
  domain: ColorFocusRange,
  boundaries: readonly ColorBoundary[] = []
): number {
  if (position <= 0) return domain[0];
  if (position >= 1) return domain[1];
  const points = anchors(domain, boundaries);
  const upper = points.findIndex((point) => position <= point.position);
  const lo = points[upper - 1];
  const hi = points[upper];
  return lo.value + ((position - lo.position) / (hi.position - lo.position)) * (hi.value - lo.value);
}

/**
 * Blend the two ramp stops bracketing `position`. Mixes in CSS rather than in JS so stops
 * can stay theme variables, as `getGlucoseHeatmapFill` does.
 */
export function rampColor(ramp: readonly RampStop[], position: number): string {
  const upper = ramp.findIndex((stop) => position <= stop.position);
  if (upper === -1) return ramp[ramp.length - 1].color;
  if (upper === 0 || ramp[upper].position === position) return ramp[upper].color;
  const lo = ramp[upper - 1];
  const hi = ramp[upper];
  const loShare = ((hi.position - position) / (hi.position - lo.position)) * 100;
  return `color-mix(in srgb, ${lo.color} ${loShare.toFixed(2)}%, ${hi.color})`;
}

export function scaleColor(value: number, scale: MetricScale): string {
  return rampColor(scale.ramp, rampPosition(value, scale.domain, scale.boundaries));
}

export function isOutsideWindow(value: number, scale: MetricScale): boolean {
  const [low, high] = scale.window;
  return (low > scale.axis[0] && value < low) || (high < scale.axis[1] && value > high);
}

/** A day's fill: its ramp color, faded when it falls outside the focus window. */
export function scaleFill(value: number, scale: MetricScale): string {
  const color = scaleColor(value, scale);
  if (!isOutsideWindow(value, scale)) return color;
  return `color-mix(in srgb, ${color} ${Math.round(100 - scale.dim)}%, transparent)`;
}

/**
 * The scale across its axis as a CSS gradient. Color is piecewise linear in value between
 * the axis ends, the domain ends, the boundaries and the values the ramp stops land on, so
 * stopping the gradient at exactly those points draws it without sampling.
 */
export function scaleGradient(scale: MetricScale): string {
  const { axis, domain, boundaries, ramp } = scale;
  const span = axis[1] - axis[0] || 1;
  const breaks = [
    ...new Set([
      axis[0],
      axis[1],
      domain[0],
      domain[1],
      ...boundaries.map((b) => b.value),
      ...ramp.map((stop) => rampValue(stop.position, domain, boundaries)),
    ]),
  ]
    .filter((value) => value >= axis[0] && value <= axis[1])
    .sort((a, b) => a - b);
  const stops = breaks.map(
    (value) => `${scaleColor(value, scale)} ${(((value - axis[0]) / span) * 100).toFixed(3)}%`
  );
  return `linear-gradient(to right in srgb, ${stops.join(", ")})`;
}

/**
 * Pins the color currently drawn at the middle of the widest gap between the window's ends
 * and the boundaries inside it, so adding a boundary leaves the scale unchanged until it moves.
 */
export function addColorBoundary(
  scale: MetricScale,
  snap: (value: number) => number = (value) => value
): ColorBoundary[] | null {
  const { boundaries, domain } = scale;
  if (boundaries.length >= MAX_COLOR_BOUNDARIES) return null;
  const low = Math.max(scale.window[0], domain[0]);
  const high = Math.min(scale.window[1], domain[1]);
  const edges = [low, ...boundaries.map((b) => b.value).filter((v) => v > low && v < high), high];
  let gap = 0;
  for (let i = 1; i < edges.length; i++) {
    if (edges[i] - edges[i - 1] > edges[gap + 1] - edges[gap]) gap = i - 1;
  }
  const value = snap((edges[gap] + edges[gap + 1]) / 2);
  const position = rampPosition(value, domain, boundaries);
  const candidate = [...boundaries, { value, position }].sort((a, b) => a.value - b.value);
  return resolveColorBoundaries(candidate, domain);
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
