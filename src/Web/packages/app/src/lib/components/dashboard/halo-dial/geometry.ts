/**
 * Pure geometry helpers for the Halo Dial. All angles are degrees measured
 * **clockwise from 12 o'clock** (so 0 = top, 90 = right/3 o'clock, 180 =
 * bottom). Prediction sweeps positive (CW); history sweeps negative (CCW).
 *
 * No layerchart imports — the dial reuses these in raw computation paths
 * (spline vertex layout, trend chevron rotation) and unit tests.
 */

export const RING_RADIUS = 55;
export const VIEWBOX = 140;
export const CENTER = VIEWBOX / 2;

/** Six degrees of arc per minute, so the canonical 45-min prediction sweeps 270°. */
export const DEGREES_PER_MINUTE = 6;

/** Visual gap reserved between history-tail and prediction-head when the spiral fallback engages. */
export const MIN_GAP_DEG = 8;

/** Maximum extra radial pixels the spiral fallback adds to the oldest reading. */
export const SPIRAL_MAX_OUTGROW_PX = 14;

/** Convert (angleDeg, radius) into a point inside the dial's viewBox. */
export function polar(angleDeg: number, radius: number): { x: number; y: number } {
  const rad = ((angleDeg - 90) * Math.PI) / 180;
  return {
    x: CENTER + Math.cos(rad) * radius,
    y: CENTER + Math.sin(rad) * radius,
  };
}

/** Sweep covered by the prediction arc, capped so we never quite close the ring. */
export function predictionSweepDeg(predictionMinutes: number): number {
  const raw = Math.max(0, predictionMinutes) * DEGREES_PER_MINUTE;
  return Math.min(raw, 360 - MIN_GAP_DEG);
}

/** Angular budget left for history once prediction has claimed its share. */
export function historySweepBudgetDeg(predictionSweep: number): number {
  return Math.max(0, 360 - predictionSweep - MIN_GAP_DEG);
}

export interface HistoryVertex {
  /** Degrees CW from 12 o'clock (so history vertices are negative — they sweep CCW). */
  angleDeg: number;
  /** Distance from the dial centre. RING_RADIUS unless the spiral fallback is active. */
  radius: number;
  value: number;
}

export interface HistoryVerticesOptions {
  /** Glucose values, oldest first. */
  values: number[];
  historyMinutes: number;
  predictionMinutes: number;
}

/**
 * Lay out history readings on the ring. The activation check is asymmetric
 * by design: the spiral is only triggered when the natural CCW sweep
 * (`historyMinutes * DEGREES_PER_MINUTE`) actually exceeds the unallocated arc
 * `360 - predictionSweep` (gap NOT subtracted). Once triggered, the sweep is
 * then clamped to `360 - predictionSweep - MIN_GAP_DEG` so the visible gap
 * is preserved, and the radius grows linearly per the Archimedean-spiral
 * fallback so the oldest reading lands at
 * `RING_RADIUS + SPIRAL_MAX_OUTGROW_PX`. Subtracting MIN_GAP_DEG from the
 * activation threshold would (incorrectly) trigger the spiral on the
 * canonical 15-min/45-min default where the natural sweeps tile exactly.
 */
export function historyVertices({
  values,
  historyMinutes,
  predictionMinutes,
}: HistoryVerticesOptions): HistoryVertex[] {
  if (values.length === 0) return [];

  const predictionSweep = predictionSweepDeg(predictionMinutes);
  const naturalSweep = Math.max(0, historyMinutes) * DEGREES_PER_MINUTE;
  const availableWithoutGap = Math.max(0, 360 - predictionSweep);
  const spiralActive = naturalSweep > availableWithoutGap;

  const angularSweep = spiralActive
    ? historySweepBudgetDeg(predictionSweep)
    : naturalSweep;

  const lastIndex = values.length - 1;

  return values.map((value, i) => {
    // i=0 is oldest → furthest CCW (angle = -angularSweep), i=lastIndex is newest at 0.
    const tFromNewest = lastIndex === 0 ? 0 : (lastIndex - i) / lastIndex;
    const angleDeg = tFromNewest === 0 ? 0 : -angularSweep * tFromNewest;
    const radius = spiralActive
      ? RING_RADIUS + SPIRAL_MAX_OUTGROW_PX * tFromNewest
      : RING_RADIUS;
    return { angleDeg, radius, value };
  });
}

/**
 * Convert a 5-minute glucose delta to a Dexcom-style trend chevron angle.
 * Mirrors `trendAngle` in the source design (concepts/shared.jsx:116-120):
 * 0° = steady (chevron points right), negative = up, positive = down,
 * clamped at ±12 mg/dL/5min so very-fast trends don't go past the ring.
 */
export function trendAngle(deltaPer5: number): number {
  const clamped = Math.max(-12, Math.min(12, deltaPer5));
  if (clamped === 0) return 0;
  return -clamped * 6;
}
