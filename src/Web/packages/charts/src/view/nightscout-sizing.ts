/**
 * Sizing rules of cgm-remote-monitor's focus chart (lib/client/renderer.js), so
 * the Nightscout theme's dots and treatment bubbles scale with the chart width
 * and focus range as there.
 */

const HOUR_MS = 60 * 60 * 1000;
const WIDTH_SMALL_DOTS = 420;
const WIDTH_BIG_DOTS = 800;

/** 1 at a three-hour focus range, growing by 1/8 for each further three hours. */
export function focusRangeAdjustment(focusMs: number): number {
  const threeHours = 3 * HOUR_MS;
  return focusMs <= threeHours
    ? 1
    : 1 + (focusMs - threeHours) / threeHours / 8;
}

/** Radius of a reading's dot. */
export function sgvDotRadius(chartWidth: number, focusMs: number): number {
  const r =
    chartWidth > WIDTH_BIG_DOTS ? 4 : chartWidth < WIDTH_SMALL_DOTS ? 2 : 3;
  return r / focusRangeAdjustment(focusMs);
}

/** Radius of a forecast dot: one less than a reading's, at most 3. */
export function forecastDotRadius(chartWidth: number, focusMs: number): number {
  const r =
    chartWidth > WIDTH_BIG_DOTS ? 4 : chartWidth < WIDTH_SMALL_DOTS ? 2 : 3;
  return Math.min(3, r - 1) / focusRangeAdjustment(focusMs);
}

/** Divisor for treatment bubbles: larger makes them smaller. */
export function bubbleScale(chartWidth: number, focusMs: number): number {
  const base =
    chartWidth < WIDTH_SMALL_DOTS ? 4 : chartWidth < WIDTH_BIG_DOTS ? 3 : 2;
  return base * focusRangeAdjustment(focusMs);
}

/**
 * Carb ratio Nightscout sizes an insulin-only bubble with when the profile
 * gives none.
 */
export const DEFAULT_BUBBLE_CARB_RATIO = 20;

export interface TreatmentBubble {
  /** Radius of the pie. */
  radius: number;
  /** Distance from the centre to the carb (above) and insulin (below) labels. */
  labelOffset: number;
  /** Font size of an amount's label; one under 1 is drawn smaller. */
  labelSize: (amount: number) => number;
}

/**
 * The pie and label geometry of a bolus/carb treatment (`calcTreatmentRadius`,
 * `prepareArc`, `appendLabels`). The pie is sized by the carbs when the entry
 * has any, else by the insulin times the carb ratio. The arcs are drawn at five
 * times R1, and a label sits on the centroid of an arc from R1 to R1 + 8/scale.
 */
export function treatmentBubble(
  carbs: number,
  insulin: number,
  carbRatio: number,
  scale: number,
  treatmentCount: number
): TreatmentBubble {
  const amount =
    carbs > 0 ? carbs : insulin > 0 ? insulin * carbRatio : carbRatio;
  const r1 = Math.sqrt(amount) / scale;
  const r3 = r1 + 8 / scale;
  const fontSize =
    (treatmentCount >= 30
      ? 40
      : 50 - Math.floor(((25 - treatmentCount) / 30) * 10)) / scale;
  return {
    radius: 5 * r1,
    labelOffset: (5 * r1 + 5 * r3) / 2,
    labelSize: (value) =>
      value < 1 ? (25 + Math.floor(value * 10)) / scale : fontSize,
  };
}

/** A dose as Nightscout rounds it: two decimals at most, no trailing zeros ("0.35"). */
export function formatDose(insulin: number): string {
  return String(Math.round(insulin * 100) / 100);
}

/**
 * The y-axis ticks of the log scale (lib/client/ticks.js), in mg/dL: the urgent
 * and target limits between fixed ends and middle.
 */
export function nightscoutTicks(
  units: "mg/dl" | "mmol",
  limits: { veryLow: number; low: number; high: number; veryHigh: number }
): number[] {
  const [bottom, middle, top] =
    units === "mmol" ? [36, 108, 396] : [40, 120, 400];
  return [
    bottom,
    limits.veryLow,
    limits.low,
    middle,
    limits.high,
    limits.veryHigh,
    top,
  ]
    .filter((v) => Number.isFinite(v))
    .sort((a, b) => a - b);
}
