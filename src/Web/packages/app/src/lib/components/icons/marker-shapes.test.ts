import { describe, it, expect } from "vitest";
import { trianglePoints } from "./marker-shapes";

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

  it("makes a bolus and a carb marker meet apex to apex on one baseline", () => {
    // The chart draws both at the same y; they must not overlap.
    const bolus = parse(trianglePoints("down", 8, 8));
    const carbs = parse(trianglePoints("up", 8, 8));

    expect(Math.max(...bolus.map(([, y]) => y))).toBe(0);
    expect(Math.min(...carbs.map(([, y]) => y))).toBe(0);
  });
});
