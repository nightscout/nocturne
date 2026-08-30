import { describe, it, expect } from "vitest";
import {
  trianglePoints,
  carbMarkerPoints,
  bolusMarkerPoints,
  MARKER_HEIGHT,
  MARKER_HEIGHT_OVERRIDE,
} from "./marker-shapes";

/** Parse an SVG points string into [x, y] pairs. */
function parse(points: string): [number, number][] {
  return points
    .trim()
    .split(/\s+/)
    .map((pair) => pair.split(",").map(Number) as [number, number]);
}

describe("trianglePoints", () => {
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
    const carbs = parse(carbMarkerPoints());
    const bolus = parse(bolusMarkerPoints());

    expect(carbs.slice(0, 2)).toEqual(bolus.slice(0, 2));
    expect(carbs.every(([, y]) => y <= 0)).toBe(true);
    expect(bolus.every(([, y]) => y >= 0)).toBe(true);
    expect(carbs.at(-1)).toEqual([0, -MARKER_HEIGHT]);
    expect(bolus.at(-1)).toEqual([0, MARKER_HEIGHT]);
  });

  it("keeps a taller override bolus on the same shared base", () => {
    const carbs = parse(carbMarkerPoints());
    const override = parse(bolusMarkerPoints(MARKER_HEIGHT_OVERRIDE));

    expect(override.slice(0, 2)).toEqual(carbs.slice(0, 2));
    expect(override.at(-1)).toEqual([0, MARKER_HEIGHT_OVERRIDE]);
  });
});
