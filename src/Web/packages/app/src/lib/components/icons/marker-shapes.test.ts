import { describe, it, expect } from "vitest";
import {
  trianglePoints,
  bolusMarkerPoints,
  BOLUS_LABEL_Y,
  CARB_LABEL_Y,
  CARB_MARKER_POINTS,
  MARKER_HEIGHT,
  MARKER_HEIGHT_OVERRIDE,
} from "./marker-shapes";

/**
 * Line box of an 8px label, the tallest the markers draw: measured at 8.8px
 * rendered, rounded up. Two rows closer than this touch.
 */
const LABEL_LINE_HEIGHT = 9;

/**
 * How far above the baseline a label may sit. The rows clear the chart's own
 * IOB/COB track and hang over the glucose track above it, which is intended and
 * unclipped; this only pins them to the marker rather than the chart.
 */
const LABEL_MAX_RISE = 40;

/** Parse an SVG points string into [x, y] pairs. */
function parse(points: string): [number, number][] {
  return points
    .trim()
    .split(/\s+/)
    .map((pair) => pair.split(",").map(Number) as [number, number]);
}

describe("marker shapes", () => {
  it("anchors every direction on the apex", () => {
    for (const direction of ["down", "up", "right"] as const) {
      const pts = parse(trianglePoints(direction, 8, 10, 30, 40));
      expect(pts, direction).toHaveLength(3);
      expect(pts.at(-1), direction).toEqual([30, 40]);
    }
  });

  it("hangs a down triangle above its apex", () => {
    // SVG y grows downward, so the base sits at a smaller y than the apex.
    const [a, b, apex] = parse(trianglePoints("down", 8, 10));
    expect(a).toEqual([-8, -10]);
    expect(b).toEqual([8, -10]);
    expect(apex).toEqual([0, 0]);
  });

  it("hangs an up triangle below its apex", () => {
    const [a, b, apex] = parse(trianglePoints("up", 8, 10));
    expect(a).toEqual([-8, 10]);
    expect(b).toEqual([8, 10]);
    expect(apex).toEqual([0, 0]);
  });

  it("puts a right triangle's base to the left of its apex", () => {
    const [a, b, apex] = parse(trianglePoints("right", 5, 9));
    expect(a).toEqual([-9, -5]);
    expect(b).toEqual([-9, 5]);
    expect(apex).toEqual([0, 0]);
  });

  it("joins a bolus and a carb marker base to base into one diamond", () => {
    // The chart draws both at the same y, so the pair must share the baseline
    // edge and lie on opposite sides of it.
    const carbs = parse(CARB_MARKER_POINTS);
    const bolus = parse(bolusMarkerPoints(MARKER_HEIGHT));

    expect(carbs.slice(0, 2)).toEqual(bolus.slice(0, 2));
    expect(carbs.every(([, y]) => y <= 0)).toBe(true);
    expect(bolus.every(([, y]) => y >= 0)).toBe(true);
    expect(carbs.at(-1)).toEqual([0, -MARKER_HEIGHT]);
    expect(bolus.at(-1)).toEqual([0, MARKER_HEIGHT]);
  });

  it("keeps a taller override bolus on the same shared base", () => {
    const carbs = parse(CARB_MARKER_POINTS);
    const normal = parse(bolusMarkerPoints(MARKER_HEIGHT));
    const override = parse(bolusMarkerPoints(MARKER_HEIGHT_OVERRIDE));

    expect(override.slice(0, 2)).toEqual(carbs.slice(0, 2));
    // Taller, and only downward: the base cannot drift off the baseline.
    expect(override.at(-1)?.[1]).toBeGreaterThan(normal.at(-1)![1]);
  });

  it("stacks both label rows above the diamond without overlapping", () => {
    // Below the baseline the chart clips, and on it a neighbouring meal's
    // triangle overdraws; only above the carb tip is clear.
    expect(CARB_LABEL_Y).toBeLessThan(-MARKER_HEIGHT);
    expect(BOLUS_LABEL_Y).toBeLessThan(-MARKER_HEIGHT);

    // The bolus row sits a full line clear of the carb row beneath it.
    expect(CARB_LABEL_Y - BOLUS_LABEL_Y).toBeGreaterThanOrEqual(
      LABEL_LINE_HEIGHT
    );

    for (const row of [CARB_LABEL_Y, BOLUS_LABEL_Y]) {
      expect(row).toBeGreaterThan(-LABEL_MAX_RISE);
    }
  });
});
