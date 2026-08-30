/**
 * Treatment marker geometry, shared by the chart markers and the legend/stat
 * icons so the two cannot drift apart.
 *
 * Every triangle is anchored on its apex, which is what the marker points at.
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
 * The chart's carb and bolus markers rest their *bases* on the one shared
 * baseline — carbs rise above it, a bolus hangs below — so a meal's pair
 * composes into a single diamond rather than an hourglass.
 *
 * That leaves the baseline as the only band clear of both triangles, so the
 * markers hang their labels off it sideways rather than stacking them above and
 * below: the track is only 18% of the chart, and a label pushed past a marker's
 * outer tip is clipped away on a short chart.
 */
export function carbMarkerPoints(): string {
  return trianglePoints(
    "up",
    MARKER_HALF_WIDTH,
    MARKER_HEIGHT,
    0,
    -MARKER_HEIGHT,
  );
}

/** @see carbMarkerPoints */
export function bolusMarkerPoints(height: number): string {
  return trianglePoints("down", MARKER_HALF_WIDTH, height, 0, height);
}
