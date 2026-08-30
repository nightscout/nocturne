/**
 * Treatment marker geometry, shared by the chart markers and the legend/stat
 * icons so the two cannot drift apart.
 *
 * A triangle from trianglePoints is anchored on its apex, which is what a
 * marker points at. The chart's own markers anchor on their base instead — see
 * CARB_MARKER_POINTS.
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
 * Both amount labels stack above the diamond, the carb amount nearest it.
 *
 * Below is unusable: the IOB/COB track is 18% of the chart (computeTrackLayout)
 * and holds the baseline at its midpoint, and being the last track its bottom
 * edge is where the chart clips — so a label under the bolus tip is cut off on
 * a short chart. The baseline itself is unusable too: every marker's triangle
 * meets it, and carb markers paint after bolus markers, so a label left there
 * is overdrawn by a neighbouring meal's triangle. Above the diamond is clear.
 */
export const CARB_LABEL_Y = -MARKER_HEIGHT - 2;
export const BOLUS_LABEL_Y = -MARKER_HEIGHT - 12;
