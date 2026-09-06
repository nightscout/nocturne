/**
 * The single set of glucose cut-points used for display bucketing.
 *
 * Prefer the tenant's own thresholds: the backend owns them and ships them with
 * the data (`ChartData.thresholds`, `Actogram.thresholds`). Pass those through
 * `resolveGlucoseThresholds` so a partial or absent payload falls back here.
 * `FALLBACK_GLUCOSE_THRESHOLDS` is for surfaces that have no access to them at
 * all, and is the only hardcoded set in the app.
 */

import type { GlucoseThresholds } from "$lib/utils/chart-colors";

export type { GlucoseThresholds };

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

/**
 * Overlay backend-supplied cut-points on the fallback. A supplied 0 is treated
 * as absent — the API sends 0 for a tenant with no profile yet, and 0 would
 * collapse the buckets onto each other.
 */
export function resolveGlucoseThresholds(
  supplied?: Partial<GlucoseThresholds> | null
): GlucoseThresholds {
  return {
    veryLow: supplied?.veryLow || FALLBACK_GLUCOSE_THRESHOLDS.veryLow,
    low: supplied?.low || FALLBACK_GLUCOSE_THRESHOLDS.low,
    high: supplied?.high || FALLBACK_GLUCOSE_THRESHOLDS.high,
    veryHigh: supplied?.veryHigh || FALLBACK_GLUCOSE_THRESHOLDS.veryHigh,
  };
}

/**
 * The cut-points in the shape `getGlucoseStatus` (@nocturne/ui/glucose-icon) and
 * `ClientSettings.thresholds` expect. Field names do not line up: there, `low`
 * and `high` are the urgent cut-points and `targetBottom`/`targetTop` are the
 * in-range band.
 */
export function toStatusThresholds(t: GlucoseThresholds): {
  low: number;
  targetBottom: number;
  targetTop: number;
  high: number;
} {
  return {
    low: t.veryLow,
    targetBottom: t.low,
    targetTop: t.high,
    high: t.veryHigh,
  };
}
