/** The change bar of a comparison row: which way it grows from the centre, and how far, as a percent of the track. */
export interface ComparisonBar {
  bar: "flat" | "up" | "down";
  /** 0 to 50: half the track is the cap. */
  barWidth: number;
}

/** The relative change, in percent, at which a row's bar reaches the end of its half of the track. */
export const COMPARISON_BAR_CAP = 60;

/**
 * The bar for a change of `changePercent` (B against A, signed). A flat change draws no bar; the
 * bar carries the sign of the change and grows with its size up to the cap, so an outlier does not
 * blow out the row.
 */
export function comparisonBar(changePercent: number, flat: boolean): ComparisonBar {
  if (flat) return { bar: "flat", barWidth: 0 };
  const magnitude = Math.min(COMPARISON_BAR_CAP, Math.abs(changePercent));
  return { bar: changePercent > 0 ? "up" : changePercent < 0 ? "down" : "flat", barWidth: (magnitude / COMPARISON_BAR_CAP) * 50 };
}
