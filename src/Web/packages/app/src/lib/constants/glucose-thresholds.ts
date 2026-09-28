/**
 * The single set of glucose cut-points used for display bucketing.
 *
 * Prefer the tenant's own thresholds: the backend owns them and ships them with
 * the data (`ChartData.thresholds`, `Actogram.thresholds`). Pass those through
 * `resolveChartThresholds` so a partial or absent payload falls back here.
 * `FALLBACK_GLUCOSE_THRESHOLDS` is for surfaces that have no access to them at
 * all, and is the only hardcoded set in the app.
 */

import type { ChartThresholdsDto } from "$lib/api/generated/nocturne-api-client";
import type { GlucoseThresholds } from "$lib/utils/chart-colors";

export type { GlucoseThresholds };

/**
 * `ChartThresholdsDto` with every field resolved; a target is null when there
 * is no profile.
 */
export type ChartThresholds = Required<
  Omit<ChartThresholdsDto, "targetLow" | "targetHigh">
> & {
  targetLow: number | null;
  targetHigh: number | null;
};

/**
 * Cut-points in mg/dL used when the tenant's own are unavailable. These are the
 * boundaries the time-in-range reports use: 70-180 in range, 54 the urgent-low
 * cut, 250 the very-high cut.
 */
export const FALLBACK_GLUCOSE_THRESHOLDS: GlucoseThresholds = {
  veryLow: 54,
  low: 70,
  high: 180,
  veryHigh: 250,
};

/** Glucose axis ceiling in mg/dL when the payload carries none. */
export const FALLBACK_GLUCOSE_Y_MAX = 300;

/**
 * Overlay backend-supplied chart thresholds on the fallbacks. A supplied 0
 * cut-point is treated as absent: the API sends 0 for a tenant with no profile
 * yet, and 0 would collapse the buckets onto each other. The axis ceiling and
 * targets are the backend's call, so any value it sends is kept.
 */
export function resolveChartThresholds(
  supplied?: ChartThresholdsDto | Partial<ChartThresholds> | null
): ChartThresholds {
  return {
    veryLow: supplied?.veryLow || FALLBACK_GLUCOSE_THRESHOLDS.veryLow,
    low: supplied?.low || FALLBACK_GLUCOSE_THRESHOLDS.low,
    high: supplied?.high || FALLBACK_GLUCOSE_THRESHOLDS.high,
    veryHigh: supplied?.veryHigh || FALLBACK_GLUCOSE_THRESHOLDS.veryHigh,
    glucoseYMax: supplied?.glucoseYMax ?? FALLBACK_GLUCOSE_Y_MAX,
    targetLow: supplied?.targetLow ?? null,
    targetHigh: supplied?.targetHigh ?? null,
  };
}
