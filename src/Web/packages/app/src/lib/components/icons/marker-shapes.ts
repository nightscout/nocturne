/**
 * Treatment marker geometry, shared by the chart markers and the legend/stat
 * icons so the two cannot drift apart.
 *
 * Every triangle is anchored on its apex, which is what the marker points at:
 * a bolus hangs above the baseline pointing down at it, carbs sit below
 * pointing up at it, so a simultaneous pair meets apex to apex.
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
