import { describe, expect, it } from "vitest";
import {
  placeCenteredLabels,
  placeTrailingLabels,
  type LabelCandidate,
} from "./marker-label-layout";

function amount(id: string, x: number, units: number): LabelCandidate<string> {
  return { item: id, x, text: `${units.toFixed(1)}U`, priority: units };
}

describe("placeCenteredLabels", () => {
  it("keeps every label when the markers are far apart", () => {
    const visible = placeCenteredLabels([
      amount("a", 0, 1),
      amount("b", 100, 1),
      amount("c", 200, 1),
    ]);
    expect(visible).toEqual(new Set(["a", "b", "c"]));
  });

  it("drops the smaller amount when two labels would overlap", () => {
    // 12px apart: a five-minute SMB cadence on a two-hour phone chart.
    const visible = placeCenteredLabels([
      amount("smb", 100, 0.05),
      amount("meal", 112, 2.0),
    ]);
    expect(visible).toEqual(new Set(["meal"]));
  });

  it("keeps the leftmost of two equal amounts", () => {
    const visible = placeCenteredLabels([
      amount("later", 112, 0.1),
      amount("earlier", 100, 0.1),
    ]);
    expect(visible).toEqual(new Set(["earlier"]));
  });

  it("thins a dense run to non-overlapping survivors, largest first", () => {
    const run = Array.from({ length: 12 }, (_, i) =>
      amount(`s${i}`, i * 12, 0.05 + (i === 5 ? 3 : 0))
    );
    const visible = placeCenteredLabels(run);

    expect(visible.has("s5")).toBe(true);
    expect(visible.size).toBeGreaterThan(1);
    expect(visible.size).toBeLessThan(run.length);

    // No two survivors sit closer than one label width.
    const xs = run
      .filter((c) => visible.has(c.item))
      .map((c) => c.x)
      .sort((a, b) => a - b);
    for (let i = 1; i < xs.length; i++) {
      expect(xs[i] - xs[i - 1]).toBeGreaterThanOrEqual(24);
    }
  });

  it("respects obstacles such as the track's own name", () => {
    const visible = placeCenteredLabels(
      [amount("edge", 10, 5)],
      [{ left: 0, right: 44 }]
    );
    expect(visible.size).toBe(0);
  });
});

describe("placeTrailingLabels", () => {
  const HALF = 8;
  const GAP = 3;

  it("shows a meal name with nothing to its left", () => {
    const visible = placeTrailingLabels(
      [{ item: "lunch", x: 200, text: "Lunch", priority: 40 }],
      [200],
      HALF,
      GAP
    );
    expect(visible).toEqual(new Set(["lunch"]));
  });

  it("does not count the meal's own paired bolus as an obstacle", () => {
    const visible = placeTrailingLabels(
      [{ item: "lunch", x: 200, text: "Lunch", priority: 40 }],
      [200, 200],
      HALF,
      GAP
    );
    expect(visible).toEqual(new Set(["lunch"]));
  });

  it("hides a name that would be painted over by a neighbouring glyph", () => {
    // "Late Night" spans 40px; a marker 20px to the left sits inside that.
    const visible = placeTrailingLabels(
      [{ item: "snack", x: 200, text: "Late Night", priority: 20 }],
      [200, 180],
      HALF,
      GAP
    );
    expect(visible.size).toBe(0);
  });

  it("hides the smaller meal's name when two names would overlap", () => {
    const visible = placeTrailingLabels(
      [
        { item: "big", x: 240, text: "Dinner", priority: 60 },
        { item: "small", x: 225, text: "Snack", priority: 10 },
      ],
      [240, 225],
      HALF,
      GAP
    );
    // The small marker's glyph column blocks "Dinner"; "Snack" then fits.
    expect(visible.has("big")).toBe(false);
    expect(visible.has("small")).toBe(true);
  });
});
