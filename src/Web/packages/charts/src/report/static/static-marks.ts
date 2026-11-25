import { line, type CurveFactory } from "d3";
import { bolusMarkerPoints, CARB_MARKER_POINTS, MARKER_HEIGHT } from "../../icons/marker-shapes.js";

/** SVG polygon points ("x,y x,y ...") moved to (dx, dy), as path data. */
export function polygonPath(points: string, dx: number, dy: number): string {
  const corners = points.trim().split(/\s+/).map((pair) => pair.split(",").map(Number));
  return corners.map(([x, y], i) => `${i === 0 ? "M" : "L"}${x + dx},${y + dy}`).join(" ") + " Z";
}

/** The dashboard's bolus glyph (hanging below its point) and carb glyph (rising above it), as path data. */
export function markerPath(kind: "bolus" | "carbs", x: number, y: number): string {
  return polygonPath(kind === "bolus" ? bolusMarkerPoints(MARKER_HEIGHT) : CARB_MARKER_POINTS, x, y);
}

/**
 * A line through the points as path data, in plot pixels. A static chart draws a line stroked with
 * a canvas gradient through this and `Path`: `Spline` reads only a colour string as its stroke.
 */
export function linePath<T>(data: readonly T[], x: (d: T) => number, y: (d: T) => number | null, curve: CurveFactory): string {
  return (
    line<T>()
      .defined((d) => y(d) !== null)
      .x(x)
      .y((d) => y(d) ?? 0)
      .curve(curve)(data) ?? ""
  );
}
