/**
 * Treatment marker geometry, shared by the chart markers and the legend/stat
 * icons so the two cannot drift apart.
 *
 * A triangle from trianglePoints is anchored on its apex, which is what a
 * marker points at. The treatment pair is the exception and anchors on the base
 * — see <see>CARB_MARKER_POINTS</see>.
 */
export type MarkerDirection = "down" | "up" | "right";

export function trianglePoints(
  direction: MarkerDirection,
  halfWidth: number,
  height: number,
  apexX = 0,
  apexY = 0
): string {
  const apex = `${apexX},${apexY}`;
  switch (direction) {
    case "down":
      return `${apexX - halfWidth},${apexY - height} ${apexX + halfWidth},${apexY - height} ${apex}`;
    case "up":
      return `${apexX - halfWidth},${apexY + height} ${apexX + halfWidth},${apexY + height} ${apex}`;
    case "right":
      return `${apexX - height},${apexY - halfWidth} ${apexX - height},${apexY + halfWidth} ${apex}`;
  }
}

/** Half-width and height of a chart treatment marker, in chart pixels. */
export const MARKER_HALF_WIDTH = 8;
export const MARKER_HEIGHT = 8;

/** A manually overridden bolus keeps the taller silhouette it had before. */
export const MARKER_HEIGHT_OVERRIDE = 12;

/**
 * The chart's carb and bolus markers rest their bases on the one shared
 * baseline — carbs rise above it, a bolus hangs below — so a meal's pair
 * composes into a single diamond.
 */
export const CARB_MARKER_POINTS = trianglePoints(
  "up",
  MARKER_HALF_WIDTH,
  MARKER_HEIGHT,
  0,
  -MARKER_HEIGHT
);

/** @see CARB_MARKER_POINTS */
export function bolusMarkerPoints(height: number): string {
  return trianglePoints("down", MARKER_HALF_WIDTH, height, 0, height);
}

/**
 * Rows for the two amount labels, the carb amount nearest the diamond. They
 * rise clear of every treatment glyph, over the glucose track above — the
 * IOB/COB track is a fifth of the chart (<see>computeTrackLayout</see>), too
 * shallow to hold them.
 *
 * They may not go below the baseline: that track is the chart's last, so its
 * bottom edge is where the chart clips. Nor on it, where every triangle meets
 * and a later-painted marker would overdraw them.
 */
export const CARB_LABEL_Y = -MARKER_HEIGHT - 2;

/** @see CARB_LABEL_Y */
export const BOLUS_LABEL_Y = -MARKER_HEIGHT - 12;
