import { describe, it, expect } from "vitest";
import {
  CENTER,
  MIN_GAP_DEG,
  RING_RADIUS,
  SPIRAL_MAX_OUTGROW_PX,
  historySweepBudgetDeg,
  historyVertices,
  polar,
  predictionSweepDeg,
  trendAngle,
} from "./geometry";

describe("polar", () => {
  it("places angle 0 at 12 o'clock", () => {
    const p = polar(0, 50);
    expect(p.x).toBeCloseTo(CENTER, 6);
    expect(p.y).toBeCloseTo(CENTER - 50, 6);
  });

  it("places angle 90 at 3 o'clock", () => {
    const p = polar(90, 50);
    expect(p.x).toBeCloseTo(CENTER + 50, 6);
    expect(p.y).toBeCloseTo(CENTER, 6);
  });

  it("places angle -90 at 9 o'clock", () => {
    const p = polar(-90, 50);
    expect(p.x).toBeCloseTo(CENTER - 50, 6);
    expect(p.y).toBeCloseTo(CENTER, 6);
  });

  it("places angle 180 at 6 o'clock", () => {
    const p = polar(180, 50);
    expect(p.x).toBeCloseTo(CENTER, 6);
    expect(p.y).toBeCloseTo(CENTER + 50, 6);
  });
});

describe("predictionSweepDeg", () => {
  it("scales 6° per minute", () => {
    expect(predictionSweepDeg(45)).toBe(270);
    expect(predictionSweepDeg(0)).toBe(0);
  });

  it("caps at 360 - MIN_GAP_DEG so the ring never closes", () => {
    expect(predictionSweepDeg(120)).toBe(360 - MIN_GAP_DEG);
    expect(predictionSweepDeg(1000)).toBe(360 - MIN_GAP_DEG);
  });

  it("clamps negative input to zero", () => {
    expect(predictionSweepDeg(-10)).toBe(0);
  });
});

describe("historySweepBudgetDeg", () => {
  it("returns 360 - predictionSweep - MIN_GAP", () => {
    expect(historySweepBudgetDeg(270)).toBe(360 - 270 - MIN_GAP_DEG);
  });

  it("never returns negative", () => {
    expect(historySweepBudgetDeg(360)).toBe(0);
  });
});

describe("historyVertices", () => {
  const values = [80, 90, 100, 110, 120]; // oldest first

  it("keeps every vertex on the ring when natural sweep fits the budget", () => {
    const verts = historyVertices({
      values,
      historyMinutes: 15, // 15*6=90° ≤ 360-270 = 90° ⇒ no spiral
      predictionMinutes: 45,
    });
    expect(verts).toHaveLength(values.length);
    for (const v of verts) {
      expect(v.radius).toBe(RING_RADIUS);
    }
  });

  it("activates spiral when natural sweep exceeds available arc", () => {
    const verts = historyVertices({
      values,
      historyMinutes: 60, // 60*6=360° > 90° ⇒ spiral
      predictionMinutes: 45,
    });
    // Oldest reading is index 0; should sit at RING_RADIUS + outgrow.
    expect(verts[0].radius).toBeCloseTo(RING_RADIUS + SPIRAL_MAX_OUTGROW_PX, 6);
    // Newest reading is the last index; should sit on the ring.
    expect(verts.at(-1)!.radius).toBe(RING_RADIUS);
    // Radii decrease monotonically newest -> oldest.
    for (let i = 1; i < verts.length; i++) {
      expect(verts[i - 1].radius).toBeGreaterThanOrEqual(verts[i].radius);
    }
  });

  it("auto-tunes outgrow regardless of how much history is requested", () => {
    const long = historyVertices({
      values: Array.from({ length: 30 }, (_, i) => 100 + i),
      historyMinutes: 360,
      predictionMinutes: 45,
    });
    expect(long[0].radius).toBeCloseTo(RING_RADIUS + SPIRAL_MAX_OUTGROW_PX, 6);
  });

  it("places the newest vertex at angle 0 (12 o'clock)", () => {
    const verts = historyVertices({
      values,
      historyMinutes: 15,
      predictionMinutes: 45,
    });
    expect(verts.at(-1)!.angleDeg).toBe(0);
  });

  it("returns an empty array when given no values", () => {
    const verts = historyVertices({
      values: [],
      historyMinutes: 60,
      predictionMinutes: 45,
    });
    expect(verts).toEqual([]);
  });
});

describe("trendAngle", () => {
  it("steady delta points right (0°)", () => {
    expect(trendAngle(0)).toBe(0);
  });

  it("rising deltas rotate up (negative angle)", () => {
    expect(trendAngle(5)).toBeLessThan(0);
  });

  it("falling deltas rotate down (positive angle)", () => {
    expect(trendAngle(-5)).toBeGreaterThan(0);
  });

  it("clamps at ±12 mg/dL/5min", () => {
    expect(trendAngle(50)).toBe(trendAngle(12));
    expect(trendAngle(-50)).toBe(trendAngle(-12));
  });
});
